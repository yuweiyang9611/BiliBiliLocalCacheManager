using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackReliabilityTests
{
    [Theory]
    [InlineData(null, 2)]
    [InlineData("invalid", 2)]
    [InlineData("0", 2)]
    [InlineData("17", 2)]
    [InlineData("1", 1)]
    [InlineData("16", 16)]
    public void TranscodeConcurrency_UsesBoundedConfiguration(string? value, int expected) =>
        Assert.Equal(expected, FfmpegCoreTranscoder.ParseConcurrencyLimit(value));

    [Fact]
    public void ProgressDeadline_AllowsHoursOfRealProgressThenStopsStalledWork()
    {
        var clock = new Clock();
        using var deadline = new TranscodeProgressDeadline(CancellationToken.None, TimeSpan.FromMinutes(10), clock);
        for (var index = 1; index <= 24; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            deadline.Observe(new("mux", null, TimeSpan.Zero, null, index, "mux"));
            Assert.False(deadline.Token.IsCancellationRequested);
        }
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(deadline.TimedOut);
        Assert.True(deadline.Token.IsCancellationRequested);
    }

    [Fact]
    public void ProgressDeadline_IgnoresRollbackWaitingElapsedAndPhaseCycling()
    {
        var clock = new Clock();
        using var deadline = new TranscodeProgressDeadline(CancellationToken.None, TimeSpan.FromMinutes(10), clock);
        deadline.Observe(new("mux", 50, TimeSpan.Zero, null, Phase: "mux"));
        deadline.Observe(new("fallback", 20, TimeSpan.Zero, null, Phase: "fallback"));
        clock.Advance(TimeSpan.FromMinutes(9));
        deadline.Observe(new("mux", 40, TimeSpan.FromHours(9), null, Phase: "mux"));
        deadline.Observe(new("fallback", 20, TimeSpan.FromHours(9), null, Phase: "fallback"));
        deadline.Observe(new("waiting", 90, TimeSpan.Zero, null, Phase: "wait"));
        deadline.Observe(new("probe", 0, TimeSpan.Zero, null, Phase: "probe"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(deadline.TimedOut);
    }

    [Fact]
    public void ProgressDeadline_LateTimerCallbackCannotCancelFreshProgress()
    {
        var clock = new Clock();
        using var deadline = new TranscodeProgressDeadline(CancellationToken.None, TimeSpan.FromMinutes(10), clock);
        clock.Advance(TimeSpan.FromMinutes(9));
        deadline.Observe(new("mux", 10, TimeSpan.Zero, null, Phase: "mux"));
        clock.FireEarly();
        Assert.False(deadline.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(deadline.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(deadline.TimedOut);
    }

    [Theory]
    [InlineData("concat")]
    [InlineData("mux")]
    [InlineData("fallback")]
    public void ProgressBeyondEstimatedDuration_RenewsIdleDeadlineUsingProcessedSeconds(string phase)
    {
        var clock = new Clock();
        using var deadline = new TranscodeProgressDeadline(CancellationToken.None, TimeSpan.FromMinutes(10), clock);
        PlaybackPreparationProgress? latest = null;
        var tracker = new FfmpegCoreTranscoder.ProgressTracker(new InlineProgress(value =>
        {
            latest = value;
            deadline.Observe(value);
        }));
        for (var index = 1; index <= 24; index++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            tracker.ReportTime("Processing", TimeSpan.FromMinutes(index), phase, TimeSpan.FromSeconds(1));
            Assert.False(deadline.Token.IsCancellationRequested);
            Assert.Equal(100, latest!.Percentage);
            Assert.Equal(index * 60, latest.ProcessedSeconds);
        }
        clock.Advance(TimeSpan.FromMinutes(9));
        tracker.ReportTime("Processing", TimeSpan.FromMinutes(23), phase, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(deadline.TimedOut);
    }

    [Fact]
    public async Task TranscodeGate_LimitsConcurrencyAndCancelledWaitDoesNotConsumeSlot()
    {
        var gate = new TranscodeConcurrencyGate(2);
        using var first = await gate.EnterAsync(CancellationToken.None);
        using var second = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        first.Dispose();
        using var next = await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using var lastCancellation = new CancellationTokenSource();
        var blocked = gate.EnterAsync(lastCancellation.Token);
        Assert.False(blocked.IsCompleted);
        lastCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task VersionProbe_TimesOutWithoutWaitingForFirstOutputLine()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 20\"")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 20\"");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        var result = await BundledFfmpegBootstrapper.ReadProcessVersionAsync(start, TimeSpan.FromMilliseconds(200))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result);
    }

    [Fact]
    public async Task VersionProbe_DrainsStderrWhileReadingVersion()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"[Console]::Error.Write(('x' * 100000)); [Console]::WriteLine('test-version')\"")
            : new ProcessStartInfo("/bin/sh", "-c \"head -c 100000 /dev/zero >&2; echo test-version\"");
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        Assert.Equal("test-version", await BundledFfmpegBootstrapper.ReadProcessVersionAsync(start, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Download_RetriesInterruptedStreamAndHandlesRangeSupport(bool supportsRange)
    {
        var path = Path.GetTempFileName();
        try
        {
            var attempts = 0;
            using var client = new HttpClient(new Handler(request =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
                    response.Content.Headers.ContentLength = 4;
                    return response;
                }
                Assert.Equal(2, request.Headers.Range!.Ranges.Single().From);
                var result = new HttpResponseMessage(supportsRange ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(supportsRange ? [3, 4] : [1, 2, 3, 4])
                };
                if (supportsRange) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 3, 4);
                return result;
            }));
            await BundledFfmpegBootstrapper.DownloadFileAsync("https://example.invalid/archive", path,
                CancellationToken.None, null, client, TimeSpan.Zero);
            Assert.Equal(2, attempts);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    public async Task Download_RetriesOnlyTransientHttpFailures(HttpStatusCode status, int expectedAttempts)
    {
        var path = Path.GetTempFileName();
        try
        {
            var attempts = 0;
            using var client = new HttpClient(new Handler(_ => { attempts++; return new HttpResponseMessage(status); }));
            await Assert.ThrowsAsync<HttpRequestException>(() => BundledFfmpegBootstrapper.DownloadFileAsync(
                "https://example.invalid/archive", path, CancellationToken.None, null, client, TimeSpan.Zero));
            Assert.Equal(expectedAttempts, attempts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StreamCopy_UnknownLengthReportsBytesAndThrottlesUiProgress()
    {
        await using var input = new MemoryStream(new byte[16 * 1024 * 1024]);
        await using var output = new MemoryStream();
        var progressCount = 0;
        long bytes = 0;
        await BundledFfmpegBootstrapper.CopyStreamAsync(input, output, null, CancellationToken.None,
            _ => progressCount++, value => bytes = value);
        Assert.Equal(input.Length, bytes);
        Assert.True(progressCount < input.Length / (128 * 1024));
    }

    [Fact]
    public async Task Download_UnknownLengthCanMakeProgressBeyondTenMinutes()
    {
        var path = Path.GetTempFileName();
        var clock = new Clock();
        try
        {
            using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TimedStream(clock, TimeSpan.FromMinutes(5)))
            }));
            await BundledFfmpegBootstrapper.DownloadFileAsync("https://example.invalid/archive", path,
                CancellationToken.None, null, client, TimeSpan.Zero, clock);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, await File.ReadAllBytesAsync(path));
            Assert.True(clock.GetTimestamp() > TimeSpan.FromMinutes(10).Ticks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Download_StalledReadsAreBoundedAndNotReportedAsUserCancellation()
    {
        var path = Path.GetTempFileName();
        var clock = new Clock();
        try
        {
            using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TimedStream(clock, TimeSpan.FromMinutes(11)))
            }));
            await Assert.ThrowsAsync<TimeoutException>(() => BundledFfmpegBootstrapper.DownloadFileAsync(
                "https://example.invalid/archive", path, CancellationToken.None, null, client, TimeSpan.Zero, clock));
        }
        finally { File.Delete(path); }
    }

    private sealed class TimedStream(Clock clock, TimeSpan perRead) : MemoryStream([1, 2, 3, 4, 5, 6, 7, 8])
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            clock.Advance(perRead);
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handle(request));
    }

    private sealed class InlineProgress(Action<PlaybackPreparationProgress> report) : IProgress<PlaybackPreparationProgress>
    {
        public void Report(PlaybackPreparationProgress value) => report(value);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        private Timer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new Timer(this, callback, state, dueTime);
        public void Advance(TimeSpan amount) { _ticks += amount.Ticks; _timer?.Tick(); }
        public void FireEarly() => _timer?.Fire();
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private long _next = clock._ticks + due.Ticks;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { _next = clock._ticks + dueTime.Ticks; return !_disposed; }
            public void Tick() { if (!_disposed && clock._ticks >= _next) Fire(); }
            public void Fire() { if (!_disposed) callback(state); }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
