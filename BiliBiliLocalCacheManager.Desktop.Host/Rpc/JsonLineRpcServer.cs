using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliBiliLocalCacheManager.Desktop.Host.Rpc;

internal sealed class JsonLineRpcServer
{
    private const int MaximumInputLineLength = 1024 * 1024;
    private const int MaximumConcurrentRequests = 32;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly DesktopHostApplication _application;
    private readonly TextReader _input;
    private readonly ProtocolWriter _writer;
    private readonly CancellationTokenSource _transportCancellation = new();
    private Exception? _transportFailure;
    private readonly ConcurrentDictionary<string, RunningRequest> _running =
        new(StringComparer.Ordinal);

    public JsonLineRpcServer(
        DesktopHostApplication application,
        TextReader input,
        TextWriter output)
        : this(application, input, new ProtocolWriter(output, SerializerOptions)) { }

    public JsonLineRpcServer(DesktopHostApplication application, TextReader input, Stream output)
        : this(application, input, new ProtocolWriter(output, SerializerOptions)) { }

    private JsonLineRpcServer(DesktopHostApplication application, TextReader input, ProtocolWriter writer)
    {
        _application = application;
        _input = input;
        _writer = writer;
        _application.ProgressReported += OnProgressReported;
    }

    public async Task RunAsync()
    {
        var tasks = new List<Task>();
        var lineReader = new LimitedLineReader(_input, MaximumInputLineLength);
        try
        {
            while (true)
            {
                var read = await lineReader.ReadAsync(_transportCancellation.Token);
                if (read.EndOfStream)
                {
                    break;
                }

                if (read.TooLong)
                {
                    await _writer.WriteErrorAsync(
                        string.Empty,
                        new RpcError(
                            "request_too_large",
                            $"An input line may not exceed {MaximumInputLineLength} characters."));
                    continue;
                }

                var line = read.Line!;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!TryParseRequest(line, out var request, out var parseId, out var parseError))
                {
                    await _writer.WriteErrorAsync(parseId, parseError!);
                    continue;
                }

                if (string.Equals(request!.Method, "cancel", StringComparison.Ordinal))
                {
                    if (_running.ContainsKey(request.Id))
                    {
                        await _writer.WriteErrorAsync(
                            request.Id,
                            new RpcError(
                                "duplicate_id",
                                $"A request with id '{request.Id}' is already running."));
                        continue;
                    }

                    await HandleCancelAsync(request);
                    continue;
                }

                if (_running.Count >= MaximumConcurrentRequests)
                {
                    await _writer.WriteErrorAsync(
                        request.Id,
                        new RpcError(
                            "server_busy",
                            $"At most {MaximumConcurrentRequests} requests may run concurrently."));
                    continue;
                }

                var cancellation = new CancellationTokenSource();
                var running = new RunningRequest(cancellation, new RequestProgressBuffer(
                    progress => _writer.WriteEventAsync("progress", progress), OnTransportFailure));
                if (!_running.TryAdd(request.Id, running))
                {
                    cancellation.Dispose();
                    await running.Progress.DisposeAsync();
                    await _writer.WriteErrorAsync(
                        request.Id,
                        new RpcError(
                            "duplicate_id",
                            $"A request with id '{request.Id}' is already running."));
                    continue;
                }

                var task = ProcessRequestAsync(request, running);
                tasks.Add(task);
                tasks.RemoveAll(candidate => candidate.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (_transportCancellation.IsCancellationRequested)
        {
            throw new IOException("The desktop protocol output closed unexpectedly.", _transportFailure);
        }
        finally
        {
            _application.ProgressReported -= OnProgressReported;
            foreach (var running in _running.Values)
            {
                TryCancel(running.Cancellation);
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // Every request reports its own failure. EOF only shuts the transport down.
            }

            foreach (var running in _running.Values)
            {
                running.Cancellation.Dispose();
            }

            _running.Clear();
            _transportCancellation.Dispose();
        }
    }

    private async Task ProcessRequestAsync(RpcRequest request, RunningRequest running)
    {
        object? result = null;
        RpcError? error = null;
        try
        {
            result = await _application.DispatchAsync(
                request.Id,
                request.Method,
                request.Parameters,
                running.Cancellation.Token);
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            error = new RpcError("cancelled", "The operation was cancelled.");
        }
        catch (RpcException exception)
        {
            error = new RpcError(exception.Code, exception.Message, exception.Details);
        }
        catch (Exception exception)
        {
            error = new RpcError(
                    "operation_failed",
                    exception.Message,
                    new { exceptionType = exception.GetType().FullName });
        }
        try
        {
            await running.Progress.CompleteAsync();
            if (error is null) await _writer.WriteResultAsync(request.Id, result);
            else await _writer.WriteErrorAsync(request.Id, error);
        }
        catch (Exception exception) { OnTransportFailure(exception); }
        finally
        {
            _running.TryRemove(request.Id, out _);
            try { await running.Progress.DisposeAsync(); }
            catch (Exception exception) { OnTransportFailure(exception); }
            running.Cancellation.Dispose();
        }
    }

    private async Task HandleCancelAsync(RpcRequest request)
    {
        try
        {
            var targetId = request.Parameters.RequireString("requestId");
            var cancelled = false;
            if (!string.Equals(targetId, request.Id, StringComparison.Ordinal) &&
                _running.TryGetValue(targetId, out var running))
            {
                try { running.Cancellation.Cancel(); cancelled = true; }
                catch (ObjectDisposedException) { /* The request completed while cancellation was being read. */ }
            }

            await _writer.WriteResultAsync(
                request.Id,
                new { requestId = targetId, cancelled });
        }
        catch (RpcException exception)
        {
            await _writer.WriteErrorAsync(
                request.Id,
                new RpcError(exception.Code, exception.Message, exception.Details));
        }
    }

    private void OnProgressReported(object? sender, HostProgressEvent progress)
    {
        if (_running.TryGetValue(progress.RequestId, out var running)) running.Progress.Report(progress);
    }

    private void OnTransportFailure(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _transportFailure, exception, null) is not null) return;
        _transportCancellation.Cancel();
        foreach (var running in _running.Values) TryCancel(running.Cancellation);
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static bool TryParseRequest(
        string line,
        out RpcRequest? request,
        out string id,
        out RpcError? error)
    {
        request = null;
        id = string.Empty;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = new RpcError("invalid_request", "The request must be a JSON object.");
                return false;
            }

            if (root.TryGetPropertyIgnoreCase("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String)
            {
                id = idElement.GetString()?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                error = new RpcError("invalid_request", "Property 'id' must be a non-empty string.");
                return false;
            }

            if (!root.TryGetPropertyIgnoreCase("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(methodElement.GetString()))
            {
                error = new RpcError("invalid_request", "Property 'method' must be a non-empty string.");
                return false;
            }

            JsonElement parameters;
            if (!root.TryGetPropertyIgnoreCase("params", out var paramsElement) ||
                paramsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                using var emptyDocument = JsonDocument.Parse("{}");
                parameters = emptyDocument.RootElement.Clone();
            }
            else if (paramsElement.ValueKind != JsonValueKind.Object)
            {
                error = new RpcError("invalid_request", "Property 'params' must be an object when present.");
                return false;
            }
            else
            {
                parameters = paramsElement.Clone();
            }

            request = new RpcRequest(id, methodElement.GetString()!.Trim(), parameters);
            return true;
        }
        catch (JsonException exception)
        {
            error = new RpcError(
                "parse_error",
                "The input line is not valid JSON.",
                new { exception.Message });
            return false;
        }
    }

    private sealed class RunningRequest(CancellationTokenSource cancellation, RequestProgressBuffer progress)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public RequestProgressBuffer Progress { get; } = progress;
    }

    private sealed class LimitedLineReader(TextReader input, int maximumLength)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;
        private bool _reachedEnd;

        public async Task<LineReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            if (_reachedEnd && _position >= _length)
            {
                return new LineReadResult(EndOfStream: true, TooLong: false, Line: null);
            }

            var builder = new System.Text.StringBuilder(Math.Min(4096, maximumLength));
            var tooLong = false;
            while (true)
            {
                if (_position >= _length)
                {
                    _length = await input.ReadAsync(_buffer.AsMemory(), cancellationToken);
                    _position = 0;
                    if (_length == 0)
                    {
                        _reachedEnd = true;
                        if (builder.Length == 0 && !tooLong)
                        {
                            return new LineReadResult(EndOfStream: true, TooLong: false, Line: null);
                        }

                        return Complete(builder, tooLong);
                    }
                }

                var character = _buffer[_position++];
                if (character == '\n')
                {
                    return Complete(builder, tooLong);
                }

                if (!tooLong)
                {
                    if (builder.Length >= maximumLength)
                    {
                        tooLong = true;
                        builder.Clear();
                    }
                    else
                    {
                        builder.Append(character);
                    }
                }
            }
        }

        private static LineReadResult Complete(System.Text.StringBuilder builder, bool tooLong)
        {
            if (tooLong)
            {
                return new LineReadResult(EndOfStream: false, TooLong: true, Line: null);
            }

            if (builder.Length > 0 && builder[^1] == '\r')
            {
                builder.Length--;
            }

            return new LineReadResult(EndOfStream: false, TooLong: false, builder.ToString());
        }
    }

    private sealed record LineReadResult(bool EndOfStream, bool TooLong, string? Line);

}
