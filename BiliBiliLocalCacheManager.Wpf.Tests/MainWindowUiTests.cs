using System.Diagnostics;
using System.IO;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using BiliBiliLocalCacheManager.Wpf.Services;
using Xunit;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class MainWindowUiTests
{
    [Fact]
    [Trait("Category", "UI")]
    public void MainWindow_ShouldExposeKeyControls()
    {
        using var workspace = new IsolatedUiWorkspace();
        RunInSta(() =>
        {
            var exePath = GetWpfExecutablePath();
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            };
            startInfo.Environment[
                ApplicationStoragePathResolver.TestModeEnvironmentVariable] = "1";
            startInfo.Environment[
                ApplicationStoragePathResolver.SettingsPathEnvironmentVariable] =
                workspace.SettingsPath;
            startInfo.Environment[
                ApplicationStoragePathResolver.TranscodeCacheRootEnvironmentVariable] =
                workspace.TranscodeCacheRoot;
            using var app = Application.Launch(startInfo);
            using var automation = new UIA3Automation();
            try
            {
                var window = Retry.WhileNull(
                    () => app.GetMainWindow(automation),
                    timeout: TimeSpan.FromSeconds(5))
                    .Result;

                Assert.NotNull(window);

                AssertControl(window, "RootPathTextBox");
                AssertControl(window, "BrowseRootButton");
                AssertControl(window, "IncludeIncompleteCheckBox");
                AssertControl(window, "ScanButton");
                AssertControl(window, "CancelOperationButton");
                AssertControl(window, "KeywordTextBox");
                AssertControl(window, "MatchModeComboBox");
                AssertControl(window, "SearchButton");
                AssertControl(window, "PlayerPreferenceComboBox");
                AssertControl(window, "StorageManagementExpander");
                AssertControl(window, "StorageOverviewSummaryTextBlock");
                AssertControl(window, "OriginalCacheStorageTextBlock");
                AssertControl(window, "TranscodeCacheSummaryTextBlock");
                AssertControl(window, "TrashStorageTextBlock");
                AssertControl(window, "LastStorageCleanupTextBlock");
                AssertControl(window, "RefreshStorageOverviewButton");
                AssertControl(window, "TranscodeCacheRetentionDaysTextBox");
                AssertControl(window, "TranscodeCacheMaxSizeGigabytesTextBox");
                AssertControl(window, "OpenTranscodeCacheButton");
                AssertControl(window, "CleanupTranscodeCacheButton");
                AssertControl(window, "ClearTranscodeCacheButton");
                AssertControl(window, "SegmentDetailDataGrid");
                AssertControl(window, "PlayButton");
                AssertControl(window, "PlayNextButton");
                AssertControl(window, "ClearQueueButton");
                AssertControl(window, "DeleteButton");
                AssertControl(window, "UndoDeleteButton");
                AssertControl(window, "OpenTrashButton");
                AssertControl(window, "PurgeTrashButton");
                AssertControl(window, "StorageSummaryTextBlock");
                AssertControl(window, "ClearButton");
                AssertControl(window, "ExportDiagnosticsButton");
                AssertControl(window, "HelpButton");
                AssertControl(window, "CacheDataGrid");
            }
            finally
            {
                EnsureApplicationClosed(app);
            }
        });
    }

    private static void EnsureApplicationClosed(Application app)
    {
        try
        {
            if (!app.HasExited)
            {
                app.Close(killIfCloseFails: true);
            }
        }
        catch
        {
            try
            {
                if (!app.HasExited)
                {
                    app.Kill();
                }
            }
            catch
            {
                // Cleanup must not hide the test assertion that caused this path.
            }
        }
    }

    private static void AssertControl(Window window, string automationId)
    {
        Assert.True(
            FindByAutomationId(window, automationId) is not null,
            $"Missing control with AutomationId '{automationId}'.");
    }

    private static AutomationElement? FindByAutomationId(Window window, string automationId)
    {
        return window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
    }

    private static string GetWpfExecutablePath()
    {
        var solutionRoot = FindSolutionRoot();
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var executablePath = Path.Combine(
            solutionRoot,
            "BiliBiliLocalCacheManager.Wpf",
            "bin",
            configuration,
            "net10.0-windows",
            "BiliBiliLocalCacheManager.Wpf.exe");

        if (File.Exists(executablePath))
        {
            return executablePath;
        }

        throw new FileNotFoundException(
            $"WPF executable for the {configuration} configuration was not found. Build the WPF project first.",
            executablePath);
    }

    private static string FindSolutionRoot()
    {
        foreach (var startPath in GetSearchRoots())
        {
            var dir = new DirectoryInfo(startPath);
            while (dir != null)
            {
                var slnxPath = Path.Combine(dir.FullName, "BiliBiliLocalCacheManager.slnx");
                if (File.Exists(slnxPath))
                {
                    return dir.FullName;
                }

                var slnPath = Path.Combine(dir.FullName, "BiliBiliLocalCacheManager.sln");
                if (File.Exists(slnPath))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        throw new DirectoryNotFoundException("Solution root not found from test base directory.");
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && visited.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static void RunInSta(Action action)
    {
        Exception? error = null;
        var resetEvent = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                resetEvent.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        resetEvent.Wait();

        if (error is not null)
        {
            throw error;
        }
    }

    private sealed class IsolatedUiWorkspace : IDisposable
    {
        public IsolatedUiWorkspace()
        {
            RootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"BiliBiliLocalCacheManager.UiTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootDirectory);
            SettingsPath = Path.Combine(RootDirectory, "settings", "settings.json");
            TranscodeCacheRoot = Path.Combine(RootDirectory, "transcode-cache");
        }

        public string RootDirectory { get; }

        public string SettingsPath { get; }

        public string TranscodeCacheRoot { get; }

        public void Dispose()
        {
            if (!Directory.Exists(RootDirectory))
            {
                return;
            }

            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            var fullRoot = Path.GetFullPath(RootDirectory);
            var relative = Path.GetRelativePath(temporaryRoot, fullRoot);
            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete UI test directory outside the temporary root: {fullRoot}");
            }

            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
