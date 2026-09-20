using System.Text;
using System.Text.Json;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed partial class DesktopHostContractTests
{
    private static JsonSerializerOptions ProtocolOptions => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task ProtocolWriter_UsesUtf8AndFlushesOncePerMessage()
    {
        using var output = new FlushCountingStream();
        var writer = new ProtocolWriter(output, ProtocolOptions);
        var title = "\u4e2d\u6587 & \"special\"\nsecond line";
        await writer.WriteResultAsync("request", new { title });
        var bytes = output.ToArray();
        Assert.NotEqual((byte)0xef, bytes[0]);
        Assert.Equal((byte)'\n', bytes[^1]);
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal(title, parsed.RootElement.GetProperty("result").GetProperty("title").GetString());
        Assert.Equal(1, output.FlushCount);
    }

    [Fact]
    public async Task ProtocolWriter_StopsSerializingOversizedResultsBeforePublishingAnyBytes()
    {
        using var output = new MemoryStream();
        var enumerated = 0;
        IEnumerable<string> Items()
        {
            for (var i = 0; i < 1_000_000; i++) { enumerated++; yield return new string('x', 100); }
        }
        var writer = new ProtocolWriter(output, ProtocolOptions, maximumBytes: 32 * 1024);
        await writer.WriteResultAsync("bounded", Items());
        Assert.InRange(enumerated, 1, 1000);
        var line = Assert.Single(Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var parsed = JsonDocument.Parse(line);
        Assert.Equal("response_too_large", parsed.RootElement.GetProperty("error").GetProperty("code").GetString());
        var bounded = new ProtocolWriter.BoundedBufferWriter(1024);
        Assert.Throws<ProtocolWriter.ResponseTooLargeException>(() => bounded.GetMemory(1025));
        Assert.True(bounded.Capacity <= 1024);
    }

    [Fact]
    public async Task ProtocolWriter_DoesNotHideBrokenOutput()
    {
        var writer = new ProtocolWriter(new BrokenOutputStream(), ProtocolOptions);
        await Assert.ThrowsAsync<IOException>(() => writer.WriteResultAsync("broken", new { success = true }));
    }

    [Fact]
    public async Task ProgressBuffer_CoalescesWithoutBlockingAndFlushesBeforeTerminalResponse()
    {
        var writes = new List<string>();
        await using var buffer = new RequestProgressBuffer(progress =>
        {
            writes.Add(progress.Current.ToString()!);
            return Task.CompletedTask;
        }, exception => Assert.Fail(exception.Message), interval: TimeSpan.FromDays(1));
        for (var i = 1; i <= 10_000; i++) buffer.Report(new HostProgressEvent("scan", "scan", "scanning", Current: i));
        Assert.Empty(writes);
        await buffer.CompleteAsync();
        writes.Add("result");
        buffer.Report(new HostProgressEvent("scan", "scan", "scanning", Current: 10_001));
        await buffer.CompleteAsync();
        Assert.Equal(new[] { "10000", "result" }, writes);
    }

    [Fact]
    public async Task ProgressBuffer_CompletionJoinsAnInFlightWrite()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<int?>();
        await using var buffer = new RequestProgressBuffer(async progress =>
        {
            started.TrySetResult();
            await release.Task;
            writes.Add(progress.Current);
        }, exception => Assert.Fail(exception.Message), interval: TimeSpan.FromMilliseconds(1));
        buffer.Report(new HostProgressEvent("scan", "scan", "scanning", Current: 1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        buffer.Report(new HostProgressEvent("scan", "scan", "completed", Current: 2));
        var completion = buffer.CompleteAsync();
        Assert.False(completion.IsCompleted);
        release.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new int?[] { 1, 2 }, writes);
    }

    [Fact]
    public async Task JsonLinesServer_OutputFailureCancelsTheInputLoop()
    {
        using var workspace = new HostTestWorkspace();
        using var input = new BlockingAfterRequestReader("{\"id\":\"health\",\"method\":\"health\"}\n");
        var server = new JsonLineRpcServer(workspace.CreateApplication(), input, new BrokenOutputStream());
        await Assert.ThrowsAsync<IOException>(() => server.RunAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken) { FlushCount++; return Task.CompletedTask; }
    }

    private sealed class BrokenOutputStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("The pipe closed."));
    }

    private sealed class BlockingAfterRequestReader(string request) : TextReader
    {
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read) { _read = true; request.AsMemory().CopyTo(buffer); return request.Length; }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
