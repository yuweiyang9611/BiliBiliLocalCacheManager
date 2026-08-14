using System.Diagnostics;
using System.IO;

namespace BiliBiliLocalCacheManager.Wpf.Services;

/// <summary>
/// 默认帮助页实现：打开输出目录中的 Help/Help.html。
/// </summary>
public sealed class HelpService : IHelpService
{
    private const string HelpRelativePath = "Help\\Help.html";

    public void OpenHelp()
    {
        var helpPath = Path.Combine(AppContext.BaseDirectory, HelpRelativePath);
        if (!File.Exists(helpPath))
        {
            throw new FileNotFoundException("Help file not found.", helpPath);
        }

        // UseShellExecute=true 让系统用默认浏览器打开 HTML
        var startInfo = new ProcessStartInfo
        {
            FileName = helpPath,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }
}
