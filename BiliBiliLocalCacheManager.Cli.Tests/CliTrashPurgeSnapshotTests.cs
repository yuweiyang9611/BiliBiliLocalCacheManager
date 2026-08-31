using BiliBiliLocalCacheManager.Cli.Commands;
using BiliBiliLocalCacheManager.Core.Application.Contracts;
using BiliBiliLocalCacheManager.Core.Application.Models;
using Spectre.Console;

namespace BiliBiliLocalCacheManager.Cli.Tests;

public sealed class CliTrashPurgeSnapshotTests
{
    [Fact]
    public void Purge_WithInteractiveConfirmation_ShouldPassTheConfirmedSnapshotToCore()
    {
        var entries = new[]
        {
            CreateEntry(101),
            CreateEntry(102)
        };
        var service = new RecordingTrashService
        {
            ListedEntries = entries,
            CurrentEntryIds = entries.Select(entry => entry.TrashPath).ToArray(),
            Statistics = CreateStatistics(managedEntryCount: 2)
        };
        string? confirmationQuestion = null;
        bool? confirmationDefault = null;
        var command = new TrashCommand(
            service,
            (question, defaultValue) =>
            {
                service.Calls.Add("confirm");
                confirmationQuestion = question;
                confirmationDefault = defaultValue;
                return true;
            });

        var exitCode = command.Execute(["purge", "--root", "cache-root"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["list", "stats", "confirm", "purge"], service.Calls);
        Assert.Equal(false, confirmationDefault);
        Assert.Contains("2 条受管条目", confirmationQuestion);
        Assert.False(service.LastIncludeUntrusted);
        Assert.Equal(
            entries.Select(entry => entry.TrashPath),
            service.LastExpectedEntryIds);
    }

    [Fact]
    public void Purge_WithYesAndIncludeUntrusted_ShouldSkipPromptAndPassTheCompleteSnapshot()
    {
        var managed = CreateEntry(201);
        var untrusted = CreateEntry(202, isRestorable: false);
        var entries = new[] { managed, untrusted };
        var service = new RecordingTrashService
        {
            ListedEntries = entries,
            CurrentEntryIds = entries.Select(entry => entry.TrashPath).ToArray(),
            Statistics = CreateStatistics(
                managedEntryCount: 1,
                untrustedLegacyEntryCount: 1,
                managedBytes: 1024 * 1024,
                untrustedLegacyBytes: 2 * 1024 * 1024)
        };
        var command = new TrashCommand(
            service,
            (_, _) => throw new InvalidOperationException(
                "--yes must not invoke the interactive confirmation callback."));

        var exitCode = command.Execute(
            ["purge", "--root", "cache-root", "--yes", "--include-untrusted"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["list", "stats", "purge"], service.Calls);
        Assert.True(service.LastIncludeUntrusted);
        Assert.Equal(
            entries.Select(entry => entry.TrashPath),
            service.LastExpectedEntryIds);
    }

    [Fact]
    public void Purge_WithIncludeUntrusted_ShouldDescribeUntrustedEntriesInConfirmation()
    {
        var entries = new[]
        {
            CreateEntry(301),
            CreateEntry(302, isRestorable: false)
        };
        var service = new RecordingTrashService
        {
            ListedEntries = entries,
            CurrentEntryIds = entries.Select(entry => entry.TrashPath).ToArray(),
            Statistics = CreateStatistics(
                managedEntryCount: 1,
                untrustedLegacyEntryCount: 1,
                managedBytes: 1024 * 1024,
                untrustedLegacyBytes: 2 * 1024 * 1024)
        };
        string? confirmationQuestion = null;
        var command = new TrashCommand(
            service,
            (question, _) =>
            {
                confirmationQuestion = question;
                return false;
            });

        var exitCode = command.Execute(
            ["purge", "--root", "cache-root", "--include-untrusted"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("1 条受管条目和 1 条旧版未验证条目", confirmationQuestion);
        Assert.Contains("3.00 MB", confirmationQuestion);
        Assert.Equal(0, service.PurgeCallCount);
    }

    [Fact]
    public void Purge_WhenSnapshotChanges_ShouldFailWithoutDeletingAnything()
    {
        var confirmedEntry = CreateEntry(401);
        var laterEntry = CreateEntry(402);
        var service = new RecordingTrashService
        {
            ListedEntries = [confirmedEntry],
            CurrentEntryIds = [confirmedEntry.TrashPath, laterEntry.TrashPath],
            Statistics = CreateStatistics(managedEntryCount: 1),
            RejectSnapshotMismatch = true
        };
        var command = new TrashCommand(
            service,
            (_, _) => throw new InvalidOperationException(
                "--yes must not invoke the interactive confirmation callback."));
        AnsiConsole.Record();

        var exitCode = command.Execute(["purge", "--root", "cache-root", "--yes"]);
        var output = AnsiConsole.ExportText();

        Assert.Equal(1, exitCode);
        Assert.Equal(1, service.PurgeCallCount);
        Assert.Equal(0, service.DeletedEntryCount);
        Assert.Equal([confirmedEntry.TrashPath], service.LastExpectedEntryIds);
        Assert.Contains("未删除任何条目", output);
        Assert.Contains("重新运行 trash purge", output);
    }

    private static CacheTrashEntry CreateEntry(long avid, bool isRestorable = true)
    {
        var trashPath = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                "blcm-cli-trash-tests",
                $"v1_{avid}_20260101T000000000Z_{Guid.NewGuid():N}"));
        return new CacheTrashEntry(
            avid,
            trashPath,
            Path.Combine("cache-root", avid.ToString()),
            DateTimeOffset.UtcNow,
            1,
            1024,
            isRestorable,
            isRestorable ? null : "身份元数据校验失败");
    }

    private static CacheTrashStatistics CreateStatistics(
        int managedEntryCount,
        int untrustedLegacyEntryCount = 0,
        long managedBytes = 0,
        long untrustedLegacyBytes = 0)
    {
        return new CacheTrashStatistics(
            "trash-root",
            managedEntryCount,
            managedEntryCount,
            managedBytes,
            0,
            0,
            null,
            untrustedLegacyEntryCount,
            untrustedLegacyEntryCount,
            untrustedLegacyBytes);
    }

    private sealed class RecordingTrashService : ICacheTrashService
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<CacheTrashEntry> ListedEntries { get; init; } = [];

        public IReadOnlyCollection<string> CurrentEntryIds { get; init; } = [];

        public CacheTrashStatistics Statistics { get; init; } =
            CreateStatistics(managedEntryCount: 0);

        public bool RejectSnapshotMismatch { get; init; }

        public bool LastIncludeUntrusted { get; private set; }

        public IReadOnlyCollection<string> LastExpectedEntryIds { get; private set; } = [];

        public int PurgeCallCount { get; private set; }

        public int DeletedEntryCount { get; private set; }

        public string GetTrashDirectory(string rootDirectory)
        {
            throw new NotSupportedException();
        }

        public CacheTrashOperationResult MoveToTrash(string rootDirectory, long avid)
        {
            throw new NotSupportedException();
        }

        public CacheTrashOperationResult Restore(
            string rootDirectory,
            long avid,
            string trashPath)
        {
            throw new NotSupportedException();
        }

        public CacheTrashStatistics GetStatistics(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("stats");
            return Statistics;
        }

        public IReadOnlyList<CacheTrashEntry> ListEntries(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("list");
            return ListedEntries;
        }

        public CacheTrashPurgeResult Purge(
            string rootDirectory,
            bool includeUntrustedLegacyEntries = false,
            IReadOnlyCollection<string>? expectedEntryIds = null)
        {
            Calls.Add("purge");
            PurgeCallCount++;
            LastIncludeUntrusted = includeUntrustedLegacyEntries;
            LastExpectedEntryIds = expectedEntryIds?.ToArray() ?? [];

            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            if (RejectSnapshotMismatch &&
                !LastExpectedEntryIds.ToHashSet(comparer).SetEquals(CurrentEntryIds))
            {
                throw new CacheTrashSnapshotMismatchException(
                    LastExpectedEntryIds.Count,
                    CurrentEntryIds.Count);
            }

            DeletedEntryCount = CurrentEntryIds.Count;
            return new CacheTrashPurgeResult(
                DeletedEntryCount,
                0,
                0,
                0,
                null);
        }
    }
}
