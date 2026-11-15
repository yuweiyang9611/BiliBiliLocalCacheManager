namespace BiliBiliLocalCacheManager.Playback.Tests;

// Starts work that a test waits on with a deadline on a thread of its own instead of the thread pool. Test bodies
// in this project run on pool threads; work queued with Task.Run starts only when the pool has a free thread, which
// on a busy machine can take seconds, and while it blocks it holds that thread. A cancellation fired by a timer
// still arrives on the pool, and the test's own pool thread is free for it while the test awaits.
internal static class OwnThread
{
    public static Task<T> Run<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static Task Run(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
