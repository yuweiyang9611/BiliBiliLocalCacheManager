using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheTrashConcurrencyTests
{
    [Fact]
    public async Task Restore_ShouldWaitForPurgeAndNeverReportPartialRestoreSuccess()
    {
        var root = CreateTempRoot();
        using var purgeEntered = new ManualResetEventSlim();
        using var allowPurge = new ManualResetEventSlim();
        using var restoreEntered = new ManualResetEventSlim();
        var service = new FileSystemCacheTrashService();
        Task<CacheTrashPurgeResult>? purgeTask = null;
        Task<CacheTrashOperationResult>? restoreTask = null;
        try
        {
            var original = Path.Combine(root, "100");
            Directory.CreateDirectory(Path.Combine(original, "nested"));
            File.WriteAllBytes(Path.Combine(original, "first.bin"), new byte[64]);
            File.WriteAllBytes(Path.Combine(original, "nested", "second.bin"), new byte[64]);
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);

            service.AfterMutationLockAcquiredForTesting = (operation, _) =>
            {
                if (operation == CacheTrashMutationOperation.Purge)
                {
                    purgeEntered.Set();
                    Assert.True(allowPurge.Wait(TimeSpan.FromSeconds(5)));
                }
                else if (operation == CacheTrashMutationOperation.Restore)
                {
                    restoreEntered.Set();
                }
            };

            purgeTask = Task.Run(() => service.Purge(root));
            Assert.True(purgeEntered.Wait(TimeSpan.FromSeconds(5)));

            var restoreRoot = OperatingSystem.IsWindows()
                ? root.ToUpperInvariant()
                : root;
            var typedRestoreTask = Task.Run(() => service.Restore(restoreRoot, 100, moved.TrashPath!));
            restoreTask = typedRestoreTask;
            Assert.False(restoreEntered.Wait(TimeSpan.FromMilliseconds(250)));

            allowPurge.Set();
            var purgeResult = await purgeTask.WaitAsync(TimeSpan.FromSeconds(5));
            var restoreResult = await typedRestoreTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, purgeResult.DeletedEntryCount);
            Assert.True(restoreEntered.IsSet);
            Assert.False(restoreResult.Succeeded);
            Assert.False(Directory.Exists(original));
            Assert.False(Directory.Exists(moved.TrashPath));
        }
        finally
        {
            allowPurge.Set();
            service.AfterMutationLockAcquiredForTesting = null;
            if (purgeTask is not null)
            {
                await IgnoreFailureAsync(purgeTask);
            }

            if (restoreTask is not null)
            {
                await IgnoreFailureAsync(restoreTask);
            }

            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Statistics_ShouldWaitForPurgeOnSameRoot()
    {
        var root = CreateTempRoot();
        using var purgeEntered = new ManualResetEventSlim();
        using var allowPurge = new ManualResetEventSlim();
        using var statisticsEntered = new ManualResetEventSlim();
        var service = new FileSystemCacheTrashService();
        Task<CacheTrashPurgeResult>? purgeTask = null;
        Task<CacheTrashStatistics>? statisticsTask = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "100"));
            File.WriteAllBytes(Path.Combine(root, "100", "payload.bin"), new byte[64]);
            var moved = service.MoveToTrash(root, 100);
            Assert.True(moved.Succeeded);

            service.AfterMutationLockAcquiredForTesting = (operation, _) =>
            {
                if (operation == CacheTrashMutationOperation.Purge)
                {
                    purgeEntered.Set();
                    Assert.True(allowPurge.Wait(TimeSpan.FromSeconds(5)));
                }
                else if (operation == CacheTrashMutationOperation.Statistics)
                {
                    statisticsEntered.Set();
                }
            };

            purgeTask = Task.Run(() => service.Purge(root));
            Assert.True(purgeEntered.Wait(TimeSpan.FromSeconds(5)));
            statisticsTask = Task.Run(() => service.GetStatistics(root));
            Assert.False(statisticsEntered.Wait(TimeSpan.FromMilliseconds(250)));

            allowPurge.Set();
            var purgeResult = await purgeTask.WaitAsync(TimeSpan.FromSeconds(5));
            var statistics = await statisticsTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, purgeResult.DeletedEntryCount);
            Assert.True(statisticsEntered.IsSet);
            Assert.Equal(0, statistics.ManagedEntryCount);
            Assert.Equal(0, statistics.PendingPurgeEntryCount);
        }
        finally
        {
            allowPurge.Set();
            service.AfterMutationLockAcquiredForTesting = null;
            if (purgeTask is not null)
            {
                await IgnoreFailureAsync(purgeTask);
            }

            if (statisticsTask is not null)
            {
                await IgnoreFailureAsync(statisticsTask);
            }

            SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MutationOnDifferentRoot_ShouldNotWaitForBlockedPurge()
    {
        var firstRoot = CreateTempRoot();
        var secondRoot = CreateTempRoot();
        using var firstPurgeEntered = new ManualResetEventSlim();
        using var allowFirstPurge = new ManualResetEventSlim();
        var service = new FileSystemCacheTrashService();
        Task? firstTask = null;
        try
        {
            service.AfterMutationLockAcquiredForTesting = (operation, root) =>
            {
                if (operation == CacheTrashMutationOperation.Purge &&
                    string.Equals(root, firstRoot, StringComparison.OrdinalIgnoreCase))
                {
                    firstPurgeEntered.Set();
                    Assert.True(allowFirstPurge.Wait(TimeSpan.FromSeconds(5)));
                }
            };

            firstTask = Task.Run(() => service.Purge(firstRoot));
            Assert.True(firstPurgeEntered.Wait(TimeSpan.FromSeconds(5)));

            var secondResult = await Task.Run(() => service.Purge(secondRoot))
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, secondResult.DeletedEntryCount);
            Assert.False(firstTask.IsCompleted);
        }
        finally
        {
            allowFirstPurge.Set();
            service.AfterMutationLockAcquiredForTesting = null;
            if (firstTask is not null)
            {
                await IgnoreFailureAsync(firstTask);
            }

            SafeDeleteDirectory(firstRoot);
            SafeDeleteDirectory(secondRoot);
        }
    }

    [Fact]
    public async Task Move_ShouldWaitForNamedMutexHeldByAnotherThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot();
        using var holderReady = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        var mutexName = FileSystemCacheTrashService.GetMutationMutexNameForTesting(root);
        var holderThread = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, mutexName);
            mutex.WaitOne();
            holderReady.Set();
            releaseHolder.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true
        };
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "100"));
            holderThread.Start();
            Assert.True(holderReady.Wait(TimeSpan.FromSeconds(2)));

            var moveTask = Task.Run(() =>
                new FileSystemCacheTrashService().MoveToTrash(root, 100));
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(moveTask.IsCompleted);

            releaseHolder.Set();
            var result = await moveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Succeeded);
            Assert.True(holderThread.Join(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseHolder.Set();
            if (holderThread.IsAlive)
            {
                holderThread.Join(TimeSpan.FromSeconds(2));
            }

            SafeDeleteDirectory(root);
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Preserve the original assertion failure during cleanup.
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"bili_trash_concurrency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
