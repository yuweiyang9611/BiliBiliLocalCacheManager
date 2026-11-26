using System.Text;
using BiliBiliLocalCacheManager.Desktop.Host.Services;

namespace BiliBiliLocalCacheManager.Desktop.Host.Tests;

public sealed class ExportDirectoryGuardTests
{
    private const int Watch = 7;
    private const uint DirectoryCreated = 0x40000100;
    private const uint QueueOverflow = 0x4000;
    private const uint WatchEnded = 0x8000;

    [Fact]
    public void LinuxCreationAllowsUnrelatedActivityAroundTheNewDirectory()
    {
        var sawCreation = false;
        var initial = Encode((Watch, DirectoryCreated, "another"), (Watch, DirectoryCreated, "batch"));
        Assert.False(ExportDirectoryGuard.ConsumeLinuxCreationEvents(initial, Watch, "batch"u8, ref sawCreation));
        Assert.True(sawCreation);
        var final = Encode((Watch, DirectoryCreated, "other"), (Watch, WatchEnded, ""));
        Assert.True(ExportDirectoryGuard.ConsumeLinuxCreationEvents(final, Watch, "batch"u8, ref sawCreation));
    }

    [Fact]
    public void LinuxCreationNeedsOneMatchingDirectoryAndACompleteQueue()
    {
        var created = (Watch, DirectoryCreated, "batch");
        var end = (Watch, WatchEnded, "");
        byte[][] incompleteOrAmbiguous =
        [
            Encode(end),
            Encode((Watch, DirectoryCreated, "unrelated"), end),
            Encode(created, created, end),
            Encode(created, (-1, QueueOverflow, ""), end),
            Encode(created, end)[..^1]
        ];
        foreach (var events in incompleteOrAmbiguous)
        {
            var sawCreation = false;
            Assert.Throws<IOException>(() =>
                ExportDirectoryGuard.ConsumeLinuxCreationEvents(events, Watch, "batch"u8, ref sawCreation));
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x40000200u)]
    [InlineData(DirectoryCreated)]
    public void LinuxCreationChecksExactNonAsciiName(uint extraMask)
    {
        const string name = "暂存";
        var expected = Encoding.UTF8.GetBytes(name);
        var created = (Watch, DirectoryCreated, name);
        var end = (Watch, WatchEnded, "");
        var sawCreation = false;
        if (extraMask == 0)
        {
            Assert.True(ExportDirectoryGuard.ConsumeLinuxCreationEvents(Encode(created, end), Watch, expected, ref sawCreation));
        }
        else
        {
            var events = Encode(created, (Watch, extraMask, name), end);
            Assert.Throws<IOException>(() => ExportDirectoryGuard.ConsumeLinuxCreationEvents(events, Watch, expected, ref sawCreation));
        }
    }

    [Fact]
    public void StagingLocationPreservesTheNameWhenThePathExceedsTheWireBudget()
    {
        const string name = ".cache-export.test.staging";
        var shortPath = Path.Combine("exports", name);
        var longPath = Path.Combine(new string('x', DesktopHostApplication.MaximumWireTextLength), name);

        Assert.Equal(shortPath, ExportDirectoryGuard.FormatStagingLocation(shortPath));
        Assert.Equal($"所选导出目录中的 {name}", ExportDirectoryGuard.FormatStagingLocation(longPath));
    }

    private static byte[] Encode(params (int Watch, uint Mask, string Name)[] events)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var (watch, mask, name) in events)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            var paddedLength = bytes.Length == 0 ? 0 : (bytes.Length + 16) / 16 * 16;
            writer.Write(watch);
            writer.Write(mask);
            writer.Write(0); // cookie
            writer.Write(paddedLength);
            writer.Write(bytes);
            writer.Write(new byte[paddedLength - bytes.Length]);
        }
        return stream.ToArray();
    }
}
