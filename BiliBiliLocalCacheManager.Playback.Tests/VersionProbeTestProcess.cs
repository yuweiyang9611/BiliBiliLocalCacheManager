using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

namespace BiliBiliLocalCacheManager.Playback.Tests;

internal static class VersionProbeTestProcess
{
    // VSTest discovers tests in the assembly. Only the isolated child invokes this entry point.
    private static int Main(string[] args)
    {
        if (args is not ["--starved-version-probe", var directory]) return 0;
        try
        {
            RunWithNoAvailablePoolWorkers(directory);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RunWithNoAvailablePoolWorkers(string directory)
    {
        ThreadPool.GetMinThreads(out _, out var minIo);
        ThreadPool.GetMaxThreads(out _, out var maxIo);
        Assert.True(ThreadPool.SetMinThreads(1, minIo) && ThreadPool.SetMaxThreads(1, maxIo));
        using var occupied = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = Task.Run(() => { occupied.Set(); release.Wait(); });
        Task<string?>? probe = null;
        try
        {
            Assert.True(occupied.Wait(TimeSpan.FromSeconds(5)), "The pool worker did not start.");
            var start = VersionProbeTests.CreateBlockingShell(directory, writesVersion: true);
            probe = BundledFfmpegBootstrapper.ReadProcessVersionAsync(start, TimeSpan.FromMilliseconds(200));
            // An OS wait keeps the assertion's own deadline independent of the exhausted pool.
            Assert.True(((IAsyncResult)probe).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)),
                "The version probe did not return while the pool worker was occupied.");
            Assert.Null(probe.GetAwaiter().GetResult());
            Directory.Delete(directory);
        }
        finally
        {
            release.Set();
            blocker.GetAwaiter().GetResult();
            // The parent has a separate deadline and owns this process tree if cleanup hangs.
            probe?.GetAwaiter().GetResult();
        }
    }
}
