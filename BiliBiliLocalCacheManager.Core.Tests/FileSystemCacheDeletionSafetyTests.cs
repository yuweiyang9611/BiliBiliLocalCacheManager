using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheDeletionSafetyTests
{
    [Fact]
    public void Delete_HoldsRootAndEveryTraversedDirectoryUntilDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("bili-delete-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "100")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(target, "nested")).FullName;
            File.WriteAllText(Path.Combine(nested, "media.mp4"), "payload");
            var visited = new List<string>();
            var service = new FileSystemCacheDeletionService
            {
                BeforeDirectoryEnumerationForTesting = path =>
                {
                    visited.Add(path);
                    Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
                    Assert.Throws<IOException>(() => Directory.Move(path, path + "-moved"));
                }
            };

            var result = service.DeleteByAvid(root, 100);

            Assert.True(result.Deleted, result.ErrorMessage);
            Assert.Equal(new[] { target, nested }, visited);
            Assert.False(Directory.Exists(target));
            Assert.False(FileSystemCacheTrashService.HasMutationGateForTesting(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Delete_SharesTheRootTransactionWithTrashOperations()
    {
        var root = Directory.CreateTempSubdirectory("bili-delete-").FullName;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? moving = null;
        Task? deleting = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "100"));
            var trash = new FileSystemCacheTrashService
            {
                AfterMutationLockAcquiredForTesting = (_, _) =>
                {
                    entered.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
            };
            moving = Task.Factory.StartNew(() => trash.MoveToTrash(root, 100),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            deleting = Task.Factory.StartNew(() => new FileSystemCacheDeletionService().DeleteByAvid(root, 100),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.Delay(100);
            Assert.False(deleting.IsCompleted);
            release.Set();
            await Task.WhenAll(moving, deleting).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(FileSystemCacheTrashService.HasMutationGateForTesting(root));
        }
        finally
        {
            release.Set();
            if (moving is not null) await moving.WaitAsync(TimeSpan.FromSeconds(5));
            if (deleting is not null) await deleting.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Delete_OnUnsupportedPlatformsFailsClosedButAllowsDryRun()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("bili-delete-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "100")).FullName;
            var media = Path.Combine(target, "media.mp4");
            File.WriteAllText(media, "payload");
            var service = new FileSystemCacheDeletionService();
            Assert.Null(service.DeleteByAvid(root, 100, dryRun: true).ErrorMessage);
            var result = service.DeleteByAvid(root, 100);
            Assert.False(result.Deleted);
            Assert.Contains("only on Windows", result.ErrorMessage);
            Assert.Equal("payload", File.ReadAllText(media));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnexpectedProgrammingFailureIsNotReportedAsAnOrdinaryDiskFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("bili-delete-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "100"));
            var service = new FileSystemCacheDeletionService
            {
                BeforeDirectoryEnumerationForTesting = _ => throw new NullReferenceException("test")
            };
            Assert.Throws<NullReferenceException>(() => service.DeleteByAvid(root, 100));
            Assert.False(FileSystemCacheTrashService.HasMutationGateForTesting(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Delete_PreflightRejectsExistingLinkBeforeDeletingAnyPayload()
    {
        var root = Directory.CreateTempSubdirectory("bili-delete-").FullName;
        var external = Directory.CreateTempSubdirectory("bili-external-").FullName;
        var target = Directory.CreateDirectory(Path.Combine(root, "100")).FullName;
        var link = Path.Combine(target, "z-link");
        var linked = false;
        try
        {
            var media = Path.Combine(target, "a-media.mp4");
            File.WriteAllText(media, "payload");
            try { Directory.CreateSymbolicLink(link, external); linked = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { return; }
            var result = new FileSystemCacheDeletionService().DeleteByAvid(root, 100);
            Assert.False(result.Deleted);
            Assert.NotNull(result.ErrorMessage);
            Assert.Equal("payload", File.ReadAllText(media));
        }
        finally
        {
            if (linked) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }
}
