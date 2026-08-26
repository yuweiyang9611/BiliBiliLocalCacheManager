using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliBiliLocalCacheManager.Desktop.Host.Rpc;

internal sealed class JsonLineRpcServer
{
    private const int MaximumInputLineLength = 1024 * 1024;
    private const int MaximumOutputLineByteCount = 64 * 1024 * 1024;
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
    private readonly ConcurrentDictionary<string, RunningRequest> _running =
        new(StringComparer.Ordinal);

    public JsonLineRpcServer(
        DesktopHostApplication application,
        TextReader input,
        TextWriter output)
    {
        _application = application;
        _input = input;
        _writer = new ProtocolWriter(output, SerializerOptions);
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
                var read = await lineReader.ReadAsync();
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
                var running = new RunningRequest(cancellation);
                if (!_running.TryAdd(request.Id, running))
                {
                    cancellation.Dispose();
                    await _writer.WriteErrorAsync(
                        request.Id,
                        new RpcError(
                            "duplicate_id",
                            $"A request with id '{request.Id}' is already running."));
                    continue;
                }

                var task = ProcessRequestAsync(request, running);
                running.Task = task;
                tasks.Add(task);
                tasks.RemoveAll(candidate => candidate.IsCompleted);
            }
        }
        finally
        {
            _application.ProgressReported -= OnProgressReported;
            foreach (var running in _running.Values)
            {
                running.Cancellation.Cancel();
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
        }
    }

    private async Task ProcessRequestAsync(RpcRequest request, RunningRequest running)
    {
        try
        {
            var result = await _application.DispatchAsync(
                request.Id,
                request.Method,
                request.Parameters,
                running.Cancellation.Token);
            await _writer.WriteResultAsync(request.Id, result);
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            await _writer.WriteErrorAsync(
                request.Id,
                new RpcError("cancelled", "The operation was cancelled."));
        }
        catch (RpcException exception)
        {
            await _writer.WriteErrorAsync(
                request.Id,
                new RpcError(exception.Code, exception.Message, exception.Details));
        }
        catch (Exception exception)
        {
            await _writer.WriteErrorAsync(
                request.Id,
                new RpcError(
                    "operation_failed",
                    exception.Message,
                    new { exceptionType = exception.GetType().FullName }));
        }
        finally
        {
            _running.TryRemove(request.Id, out _);
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
                running.Cancellation.Cancel();
                cancelled = true;
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
        _writer.WriteEventAsync("progress", progress).GetAwaiter().GetResult();
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

    private sealed class RunningRequest(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? Task { get; set; }
    }

    private sealed class LimitedLineReader(TextReader input, int maximumLength)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;
        private bool _reachedEnd;

        public async Task<LineReadResult> ReadAsync()
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
                    _length = await input.ReadAsync(_buffer.AsMemory());
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

    private sealed class ProtocolWriter(TextWriter output, JsonSerializerOptions serializerOptions)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task WriteResultAsync(string id, object? result)
        {
            var line = JsonSerializer.Serialize(new { id, result }, serializerOptions);
            if (Encoding.UTF8.GetByteCount(line) > MaximumOutputLineByteCount)
            {
                await WriteErrorAsync(
                    id,
                    new RpcError(
                        "response_too_large",
                        "The response exceeds the 64 MiB desktop protocol limit. Narrow the search or scan a smaller cache root."));
                return;
            }

            await WriteLineAsync(line);
        }

        public Task WriteErrorAsync(string id, RpcError error) =>
            WriteAsync(new { id, error });

        public Task WriteEventAsync(string eventName, object payload) =>
            WriteAsync(new { @event = eventName, payload });

        private async Task WriteAsync(object message)
        {
            var line = JsonSerializer.Serialize(message, serializerOptions);
            await WriteLineAsync(line);
        }

        private async Task WriteLineAsync(string line)
        {
            await _gate.WaitAsync();
            try
            {
                await output.WriteLineAsync(line);
                await output.FlushAsync();
            }
            catch (IOException)
            {
                // The parent closed stdout. Stopping its process will terminate this host.
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
