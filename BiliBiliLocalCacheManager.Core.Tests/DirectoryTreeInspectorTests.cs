using BiliBiliLocalCacheManager.Core.Infrastructure.Management;

namespace BiliBiliLocalCacheManager.Core.Tests;

public sealed class DirectoryTreeInspectorTests
{
    [Fact]
    public void Inspect_CountsNestedFilesAndPreservesCancellation()
    {
        var root = Directory.CreateTempSubdirectory("bili-inspect-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a"), new byte[7]);
            var child = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            File.WriteAllBytes(Path.Combine(child, "b"), new byte[11]);
            var result = DirectoryTreeInspector.Inspect(root, CancellationToken.None);
            Assert.Equal(2, result.FileCount);
            Assert.Equal(18, result.TotalBytes);
            using var source = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                DirectoryTreeInspector.Inspect(root, source.Token, source.Cancel));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Inspect_RechecksDirectoryWhenItChangesAfterEnumeration()
    {
        var root = Directory.CreateTempSubdirectory("bili-inspect-").FullName;
        var external = Directory.CreateTempSubdirectory("bili-external-").FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        var moved = child + "-moved";
        var replaced = false;
        try
        {
            File.WriteAllText(Path.Combine(external, "external.bin"), "external");
            var error = Record.Exception(() => DirectoryTreeInspector.Inspect(
                root, CancellationToken.None, () =>
                {
                    if (replaced) return;
                    Directory.Move(child, moved);
                    try { Directory.CreateSymbolicLink(child, external); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                    {
                        Directory.Move(moved, child);
                        return;
                    }
                    replaced = true;
                }));
            if (replaced) Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("external", File.ReadAllText(Path.Combine(external, "external.bin")));
        }
        finally
        {
            if (replaced) Directory.Delete(child);
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }
}
