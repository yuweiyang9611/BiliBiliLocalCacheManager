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

    public static (Thread Thread, Task Completion) Start(Action work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        thread.Start();
        return (thread, completion.Task);
    }

    // Use only when the work's sole blocking operation is the lock under test. Observing this state proves that
    // the waiter reached the blocking call; a thread-start signal alone would still permit pre-cancellation.
    public static void WaitUntilBlocked((Thread Thread, Task Completion) worker, TimeSpan timeout)
    {
        Assert.True(SpinWait.SpinUntil(() => worker.Completion.IsCompleted ||
            (worker.Thread.ThreadState & ThreadState.WaitSleepJoin) != 0, timeout),
            $"The worker did not block (thread {worker.Thread.ThreadState}, task {worker.Completion.Status}).");
        if (worker.Completion.IsCompleted)
        {
            worker.Completion.GetAwaiter().GetResult();
            Assert.Fail("The worker completed without waiting for the held lock.");
        }
    }

    public static async Task JoinAsync((Thread Thread, Task Completion) worker, TimeSpan timeout)
    {
        Assert.True(worker.Thread.Join(timeout), "The worker did not exit after its held lock was released.");
        await JoinAsync(worker.Completion, timeout);
    }

    public static async Task JoinAsync(Task? worker, TimeSpan timeout)
    {
        if (worker is null) return;
        try { await worker.WaitAsync(timeout); }
        catch (OperationCanceledException) { } // Cancellation is the expected outcome of these waiters.
    }
}
