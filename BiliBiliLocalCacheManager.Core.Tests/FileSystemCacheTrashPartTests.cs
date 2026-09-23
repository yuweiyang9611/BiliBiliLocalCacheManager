using System.Text.Json;
using BiliBiliLocalCacheManager.Core.Infrastructure.Management;
using BiliBiliLocalCacheManager.Core.Infrastructure.Scanning;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class FileSystemCacheTrashPartTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"part-trash-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void Part_RoundTripsWhileSiblingAndParentRemain()
    {
        var first = CreatePart(100, "c1", 1);
        var second = CreatePart(100, "c2", 2);
        var service = new FileSystemCacheTrashService();
        var segment = new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments.Single(item => item.PageIndex == 1);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        Assert.True(moved.Succeeded, moved.ErrorMessage);
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        var entry = Assert.Single(service.ListEntries(_root));
        Assert.True(entry.IsRestorable, entry.UnavailableReason);
        Assert.Equal(first, entry.OriginalPath);
        Assert.True(service.Restore(_root, 100, moved.TrashPath!).Succeeded);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
        Assert.Empty(service.ListEntries(_root));
    }

    [Fact]
    public void Restore_RefusesOccupiedPartButOtherPartCanBeRestored()
    {
        var first = CreatePart(100, "c1", 1);
        CreatePart(100, "c2", 2);
        var service = new FileSystemCacheTrashService();
        var segments = new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments;
        var targets = segments.Select(segment => service.CapturePartTarget(_root, segment)).ToArray();
        var moved = targets.Select(target => service.MovePartToTrash(_root, target)).ToArray();
        Assert.All(moved, result => Assert.True(result.Succeeded, result.ErrorMessage));
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "sentinel"), "keep");
        var conflicted = moved.Single(item => item.OriginalPath == first);
        Assert.False(service.Restore(_root, 100, conflicted.TrashPath!).Succeeded);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(first, "sentinel")));
        Assert.True(service.Restore(_root, 100, moved.Single(item => item != conflicted).TrashPath!).Succeeded);
        Assert.Single(service.ListEntries(_root));
    }

    [Fact]
    public void Move_RejectsSourceEntryChangedAfterCapture()
    {
        var first = CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var target = service.CapturePartTarget(_root, segment);
        File.AppendAllText(Path.Combine(first, "entry.json"), " ");
        var result = service.MovePartToTrash(_root, target);
        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(first));
    }

    [Fact]
    public void Move_RejectsReplacedDirectoryEvenWithIdenticalMetadata()
    {
        var first = CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var target = service.CapturePartTarget(_root, segment);
        MoveFixtureDirectory(first, first + "-old");
        CreatePart(100, "c1", 1);
        var result = service.MovePartToTrash(_root, target);
        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(first + "-old"));
    }

    [Fact]
    public void Restore_RecreatesMissingParentAndListsPageIdentity()
    {
        CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        Directory.Delete(Path.Combine(_root, "100"));
        Assert.Equal(1, Assert.Single(service.ListEntries(_root)).PageIndex);
        var result = service.Restore(_root, 100, moved.TrashPath!);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
    }

    [Fact]
    public void MetadataFailure_DoesNotMovePartOrSibling()
    {
        var first = CreatePart(100, "c1", 1);
        var second = CreatePart(100, "c2", 2);
        var service = new FileSystemCacheTrashService { BeforeTrashMetadataWriteForTesting = _ => throw new IOException("write denied") };
        var segment = new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments.Single(item => item.PageIndex == 1);
        var result = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        Assert.False(result.Succeeded);
        Assert.Contains("write denied", result.ErrorMessage);
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void Move_RevalidatesPartAfterLateReplacementAttempt()
    {
        var first = CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var target = service.CapturePartTarget(_root, segment);
        service.BeforeMoveRenameForTesting = source =>
        {
            Directory.Move(source, source + "-previous");
            CreatePart(100, "c1", 1);
        };
        var result = service.MovePartToTrash(_root, target);
        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(first));
        Assert.Empty(service.ListEntries(_root));
    }

    [Fact]
    public void Restore_RejectsLinkedParentWithoutTouchingOutside()
    {
        CreatePart(100, "c1", 1);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        var parent = Path.Combine(_root, "100");
        Directory.Delete(parent);
        try { Directory.CreateSymbolicLink(parent, outside); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314) { return; }
        try
        {
            var result = service.Restore(_root, 100, moved.TrashPath!);
            Assert.False(result.Succeeded);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
            Assert.True(Directory.Exists(moved.TrashPath));
        }
        finally { Directory.Delete(parent); }
    }

    [Fact]
    public void PartMetadata_MalformedFieldsRemainListedAsBlocked()
    {
        CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        var path = Path.Combine(moved.TrashPath!, ".trash-info.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"PageIndex\": 1", "\"PageIndex\": \"bad\""));
        Assert.False(Assert.Single(service.ListEntries(_root)).IsRestorable);
        Assert.True(Directory.Exists(moved.TrashPath));
    }

    [Theory]
    [InlineData("\"PageIndex\": 1", "\"PageIndex\": 999")]
    [InlineData("100/c1", "100/another-part")]
    public void PartMetadata_CannotRewriteTheConfirmedPageOrRestoreDestination(string before, string after)
    {
        CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        Assert.True(moved.Succeeded, moved.ErrorMessage);
        var path = Path.Combine(moved.TrashPath!, ".trash-info.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(before, after));
        Assert.False(Assert.Single(service.ListEntries(_root)).IsRestorable);
        Assert.ThrowsAny<Exception>(() => service.Restore(_root, 100, moved.TrashPath!));
        Assert.True(Directory.Exists(moved.TrashPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "100", "another-part")));
    }

    [Fact]
    public void PartEntries_ParticipateInStatisticsAndConfirmedPurge()
    {
        if (!OperatingSystem.IsWindows()) return;
        CreatePart(100, "c1", 1);
        var sibling = CreatePart(100, "c2", 2);
        var service = new FileSystemCacheTrashService();
        var segment = new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments.Single(item => item.PageIndex == 1);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        Assert.True(moved.Succeeded, moved.ErrorMessage);
        Assert.Equal(1, service.GetStatistics(_root).ManagedEntryCount);
        service.Purge(_root, expectedEntryIds: [moved.TrashPath!]);
        Assert.Empty(service.ListEntries(_root));
        Assert.True(Directory.Exists(sibling));
    }

    [Fact]
    public void PartMetadata_RejectsEscapingRestorePathAndFutureSchema()
    {
        CreatePart(100, "c1", 1);
        var service = new FileSystemCacheTrashService();
        var segment = Assert.Single(new FileSystemCacheIndexBuilder().BuildIndex(_root).ByAvid[100].Segments);
        var moved = service.MovePartToTrash(_root, service.CapturePartTarget(_root, segment));
        Assert.True(moved.Succeeded, moved.ErrorMessage);
        var path = Path.Combine(moved.TrashPath!, ".trash-info.json");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("100/c1", "../escape"));
        Assert.False(Assert.Single(service.ListEntries(_root)).IsRestorable);
        Assert.ThrowsAny<Exception>(() => service.Restore(_root, 100, moved.TrashPath!));
        File.WriteAllText(path, original.Replace("\"SchemaVersion\": 2", "\"SchemaVersion\": 999"));
        Assert.False(Assert.Single(service.ListEntries(_root)).IsRestorable);
        Assert.True(Directory.Exists(moved.TrashPath));
    }

    private string CreatePart(long avid, string name, int page)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, avid.ToString(), name)).FullName;
        File.WriteAllText(Path.Combine(path, "entry.json"), JsonSerializer.Serialize(new
        {
            avid, title = "video", type_tag = "80", is_completed = true,
            total_bytes = 4, downloaded_bytes = 4, total_time_milli = 1000,
            page_data = new { cid = page, page, part = $"part-{page}" }
        }));
        Directory.CreateDirectory(Path.Combine(path, "80"));
        File.WriteAllText(Path.Combine(path, "80", "0.blv"), "data");
        return path;
    }

    private static void MoveFixtureDirectory(string source, string destination)
    {
        // Retry transient Windows sharing errors only during fixture setup, never the service operation.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (IOException exception) when (OperatingSystem.IsWindows() && attempt < 20 &&
                (exception.HResult & 0xffff) is 5 or 32)
            {
                Thread.Sleep(25);
            }
        }
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
