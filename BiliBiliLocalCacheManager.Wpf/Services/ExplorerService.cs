using System.Diagnostics;
using System.IO;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class ExplorerService : IExplorerService
{
    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path must not be null or empty.", nameof(folderPath));
        }

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {folderPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = folderPath,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

}
