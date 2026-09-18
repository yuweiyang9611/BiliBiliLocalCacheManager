using System.Globalization;
using System.Text;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;

public sealed partial class PlaybackArtifactStore
{
    private const string ProtectionSuffix = ".protected-until";

    public void ProtectUntilIfManaged(string path, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!IsManagedArtifactFile(file)) return;
        EnsurePathIsInsideRoot(file.FullName);
        using var lease = AcquireCrossProcessLock(file.FullName, cancellationToken, null);
        if (!file.Exists) throw new FileNotFoundException("The prepared artifact was removed.", path);
        WriteProtection(file.FullName, until);
    }

    private void WriteProtection(string path, DateTimeOffset until)
    {
        var metadata = path + ProtectionSuffix;
        EnsurePathIsInsideRoot(metadata);
        var previous = ReadProtection(path);
        if (previous >= until) return;
        var temporary = metadata + "." + Guid.NewGuid().ToString("N") + ".writing";
        try
        {
            File.WriteAllText(temporary, until.UtcTicks.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, metadata, true);
        }
        finally { TryDeleteFile(temporary); }
    }

    private DateTimeOffset ReadProtection(string path)
    {
        var metadata = path + ProtectionSuffix;
        try
        {
            EnsurePathIsInsideRoot(metadata);
            if (!File.Exists(metadata)) return DateTimeOffset.MinValue;
            if (new FileInfo(metadata).Length > 64) return DateTimeOffset.MaxValue;
            return new DateTimeOffset(long.Parse(File.ReadAllText(metadata), CultureInfo.InvariantCulture), TimeSpan.Zero);
        }
        catch (FileNotFoundException) { return DateTimeOffset.MinValue; }
        catch (DirectoryNotFoundException) { return DateTimeOffset.MinValue; }
        catch { return DateTimeOffset.MaxValue; }
    }

    private bool HasActiveProtection(string path) => ReadProtection(path) > _timeProvider.GetUtcNow();

    public string CreatePlaylist(IReadOnlyList<PlaybackQueueItem> items, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(RootDirectory, "0", "Page_0", Guid.NewGuid().ToString("N")[..24] + ".m3u8");
        EnsurePathIsInsideRoot(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = AcquireCrossProcessLock(path, cancellationToken, null);
        var lines = new List<string> { "#EXTM3U" };
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaybackBatchLauncher.ValidateLocalFile(item.Path);
            lines.Add("#EXTINF:-1," + item.Title.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' '));
            lines.Add(Path.GetFullPath(item.Path));
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteProtection(path, until);
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
            return path;
        }
        catch { TryDeleteFile(path); TryDeleteFile(path + ProtectionSuffix); throw; }
    }
}
