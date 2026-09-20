using System.Buffers;
using System.Text;
using System.Text.Json;

namespace BiliBiliLocalCacheManager.Desktop.Host.Rpc;

internal sealed class ProtocolWriter
{
    internal const int MaximumLineBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stream? _stream;
    private readonly TextWriter? _textWriter;
    private readonly JsonSerializerOptions _options;
    private readonly int _maximumBytes;
    private static readonly byte[] LineFeed = [(byte)'\n'];

    public ProtocolWriter(Stream output, JsonSerializerOptions options, int maximumBytes = MaximumLineBytes)
        => (_stream, _options, _maximumBytes) = (output, options, maximumBytes);

    // Retained for in-process protocol tests and embedders; the executable writes UTF-8 directly.
    public ProtocolWriter(TextWriter output, JsonSerializerOptions options, int maximumBytes = MaximumLineBytes)
        => (_textWriter, _options, _maximumBytes) = (output, options, maximumBytes);

    public async Task WriteResultAsync(string id, object? result)
    {
        try { await WriteAsync(new { id, result }); }
        catch (ResponseTooLargeException)
        {
            await WriteErrorAsync(id, new RpcError("response_too_large",
                "The response exceeds the desktop protocol limit. Narrow the query or request a smaller page."));
        }
    }

    public async Task WriteErrorAsync(string id, RpcError error)
    {
        try { await WriteAsync(new { id, error }); }
        catch (ResponseTooLargeException)
        {
            await WriteAsync(new { id, error = new RpcError("response_too_large", "The error details exceed the desktop protocol limit.") });
        }
    }

    public Task WriteEventAsync(string eventName, object payload) => WriteAsync(new { @event = eventName, payload });

    private async Task WriteAsync(object message)
    {
        // Serializing under the gate bounds total transport buffering, not merely each concurrent request.
        await _gate.WaitAsync();
        try
        {
            var buffer = new BoundedBufferWriter(_maximumBytes);
            using (var json = new Utf8JsonWriter(buffer))
                JsonSerializer.Serialize(json, message, _options);
            if (_stream is not null)
            {
                await _stream.WriteAsync(buffer.WrittenMemory);
                await _stream.WriteAsync(LineFeed);
                await _stream.FlushAsync();
            }
            else
            {
                await _textWriter!.WriteAsync(Encoding.UTF8.GetString(buffer.WrittenMemory.Span));
                await _textWriter.WriteAsync('\n');
                await _textWriter.FlushAsync();
            }
        }
        finally { _gate.Release(); }
    }

    internal sealed class BoundedBufferWriter(int maximumBytes) : IBufferWriter<byte>
    {
        private byte[] _buffer = [];
        private int _written;
        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);
        internal int Capacity => _buffer.Length;

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) { EnsureCapacity(sizeHint); return _buffer.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) { EnsureCapacity(sizeHint); return _buffer.AsSpan(_written); }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            var required = Math.Max(sizeHint, 1);
            if (required > maximumBytes - _written) throw new ResponseTooLargeException();
            if (required <= _buffer.Length - _written) return;
            var capacity = (int)Math.Min(maximumBytes, Math.Max((long)_written + required, Math.Max(4096L, (long)_buffer.Length * 2)));
            Array.Resize(ref _buffer, capacity);
        }
    }

    internal sealed class ResponseTooLargeException : Exception;
}
