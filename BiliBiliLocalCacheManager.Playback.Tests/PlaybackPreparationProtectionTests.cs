using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

namespace BiliBiliLocalCacheManager.Playback.Tests;

public sealed class PlaybackPreparationProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blcm-preparation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LargePreparation_RenewsBeyondSixHoursAndExpiresAfterDisposal()
    {
        var clock = new ManualClock();
        var store = new PlaybackArtifactStore(_root, timeProvider: clock);
        var other = new PlaybackArtifactStore(_root, timeProvider: clock);
        var protection = new PlaybackPreparationProtection(store, timeProvider: clock);
        var paths = Enumerable.Range(1, 70).Select(Media).ToArray();
        foreach (var path in paths) protection.Register(path);
        for (var i = 0; i < 7; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(59));
            Assert.Equal(0, other.Clear().DeletedFileCount);
        }
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        await protection.DisposeAsync();
        Assert.Equal(0, clock.ActiveTimers);
        clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(70, other.Clear().DeletedFileCount);
    }

    [Fact]
    public async Task MultipleRequestsAndFinalPlaybackProtection_NeverShortenProtection()
    {
        var clock = new ManualClock();
        var store = new PlaybackArtifactStore(_root, timeProvider: clock);
        var first = new PlaybackPreparationProtection(store, timeProvider: clock);
        var second = new PlaybackPreparationProtection(store, timeProvider: clock);
        var path = Media(1);
        first.Register(path);
        second.Register(path);
        await first.DisposeAsync();
        var until = clock.GetUtcNow().AddHours(20);
        store.ProtectUntilIfManaged(path, until);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(until.UtcTicks, long.Parse(File.ReadAllText(path + ".protected-until")));
        await second.DisposeAsync();
        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(0, store.Clear().DeletedFileCount);
        clock.Advance(TimeSpan.FromHours(14));
        Assert.Equal(1, store.Clear().DeletedFileCount);
    }

    [Fact]
    public async Task RenewalFailure_CancelsPreparationAndRetainsTheReason()
    {
        var clock = new ManualClock();
        var store = new PlaybackArtifactStore(_root, timeProvider: clock);
        await using var protection = new PlaybackPreparationProtection(store, timeProvider: clock);
        var path = Media(1);
        protection.Register(path);
        File.Delete(path);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsType<FileNotFoundException>(protection.Failure);
        Assert.True(protection.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelledPreparation_DoesNotRefreshOrEraseExistingProtection()
    {
        var clock = new ManualClock();
        using var cancellation = new CancellationTokenSource();
        var store = new PlaybackArtifactStore(_root, timeProvider: clock);
        await using var protection = new PlaybackPreparationProtection(store, cancellation.Token, clock);
        var path = Media(1);
        protection.Register(path);
        var metadata = File.ReadAllText(path + ".protected-until");
        cancellation.Cancel();
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(metadata, File.ReadAllText(path + ".protected-until"));
        Assert.Null(protection.Failure);
        Assert.Equal(0, store.Clear().DeletedFileCount);
        clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(1, store.Clear().DeletedFileCount);
    }

    [Fact]
    public async Task StoppingRenewal_KeepsTheRequestTokenUsableForFinalLaunch()
    {
        var clock = new ManualClock();
        await using var protection = new PlaybackPreparationProtection(new PlaybackArtifactStore(_root), timeProvider: clock);
        protection.Register(Media(1));
        await protection.StopAsync();
        clock.Advance(TimeSpan.FromHours(7));
        Assert.False(protection.Token.IsCancellationRequested);
        Assert.Null(protection.Failure);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task RenewalWaitingForFileLock_DoesNotBlockStateAndCanBeStopped()
    {
        var clock = new ManualClock();
        var store = new PlaybackArtifactStore(_root, timeProvider: clock);
        await using var protection = new PlaybackPreparationProtection(store, timeProvider: clock);
        var path = Media(1);
        protection.Register(path);
        using var heldLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var renewal = Task.Run(() => clock.Advance(TimeSpan.FromMinutes(1)));
        await Task.Delay(100);
        Assert.Null(await Task.Run(() => protection.Failure).WaitAsync(TimeSpan.FromSeconds(2)));
        await protection.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await renewal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(protection.Failure);
    }

    [Fact]
    public void CleanupAndArtifactTouch_UseInjectedClock()
    {
        var clock = new ManualClock();
        var store = new PlaybackArtifactStore(_root, timeProvider: clock);
        var source = Path.Combine(_root, "source.mp4");
        Directory.CreateDirectory(_root);
        File.WriteAllText(source, "source");
        var plan = Models.CachePlaybackPlan.Playable(100, "title", 1, "part", "c_1", _root,
            "NewDash", Models.CachePlaybackMaterialKind.SingleFile, [source]);
        var artifact = store.GetOrCreate(plan, ".mp4", path => File.WriteAllText(path, "media"));
        clock.Advance(TimeSpan.FromDays(40));
        var options = new Models.PlaybackArtifactCleanupOptions { MaxAge = TimeSpan.FromDays(30) };
        Assert.Equal(1, store.PreviewCleanup(options).CandidateFileCount);
        store.GetOrCreate(plan, ".mp4", _ => throw new InvalidOperationException("Should reuse"));
        Assert.Equal(clock.GetUtcNow().UtcDateTime, File.GetLastWriteTimeUtc(artifact.OutputPath));
        Assert.Equal(0, store.Cleanup(options).DeletedFileCount);
    }

    private string Media(int i)
    {
        var directory = Path.Combine(_root, i.ToString(), "Page_1");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, i.ToString("x24") + ".mp4");
        File.WriteAllText(path, "media");
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<ManualTimer> _timers = [];
        public int ActiveTimers => _timers.Count(timer => !timer.Disposed);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) timer.Tick();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private DateTimeOffset _next = clock.GetUtcNow() + due;
            private TimeSpan _period = period;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
            {
                if (Disposed) return false;
                _next = clock.GetUtcNow() + dueTime;
                _period = nextPeriod;
                return true;
            }
            public void Tick()
            {
                if (Disposed || clock.GetUtcNow() < _next) return;
                _next = clock.GetUtcNow() + _period;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
