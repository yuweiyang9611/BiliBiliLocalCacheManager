using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BiliBiliLocalCacheManager.Core.Application.Services;
using BiliBiliLocalCacheManager.Playback.Services;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using BiliBiliLocalCacheManager.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using CoreContracts = BiliBiliLocalCacheManager.Core.Application.Contracts;
using PlaybackContracts = BiliBiliLocalCacheManager.Playback.Contracts;

namespace BiliBiliLocalCacheManager.Wpf;

/// <summary>
/// 应用程序入口，负责初始化依赖注入容器。
/// </summary>
public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private CancellationTokenSource? _prewarmCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        InstallGlobalExceptionHandlers();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        StartFfmpegPrewarm(_serviceProvider);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        try
        {
            _prewarmCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _prewarmCts?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private void ApplyTheme()
    {
        AppThemePalette.Apply(Resources, AppThemePalette.DetectSystemVariant());
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        // 系统在深浅色之间切换时同步刷新自定义画刷，避免只有内置控件跟着变。
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    /// <summary>
    /// 提前在后台准备 FFmpeg。失败只记录诊断事件，不打扰用户。
    /// </summary>
    private void StartFfmpegPrewarm(IServiceProvider provider)
    {
        var prewarmService = provider.GetService<PlaybackContracts.IFfmpegPrewarmService>();
        if (prewarmService is null)
        {
            return;
        }

        var recorder = provider.GetService<IDiagnosticEventRecorder>();
        _prewarmCts = new CancellationTokenSource();
        var token = _prewarmCts.Token;

        _ = Task.Run(
            async () =>
            {
                var result = await prewarmService.PrewarmAsync(token).ConfigureAwait(false);
                recorder?.Record(new DiagnosticEvent(
                    DateTimeOffset.UtcNow,
                    "FfmpegPrewarm",
                    result.Succeeded ? DiagnosticEventLevel.Information : DiagnosticEventLevel.Warning,
                    result.Message));
            },
            token);
    }

    private void InstallGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var reportPath = CrashReportWriter.TryWrite(e.Exception, "DispatcherUnhandledException");
        var message = reportPath is null
            ? $"发生未处理的错误：{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}应用会尝试继续运行。"
            : $"发生未处理的错误：{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
              $"详细信息已保存到：{Environment.NewLine}{reportPath}{Environment.NewLine}{Environment.NewLine}" +
              "应用会尝试继续运行。";

        try
        {
            System.Windows.MessageBox.Show(
                message,
                "出错了",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // 连提示框都弹不出来时不再补救，至少崩溃报告已经落盘。
        }

        // 已经记录并告知用户，继续运行比直接消失更可取。
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CrashReportWriter.TryWrite(exception, "AppDomainUnhandledException");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashReportWriter.TryWrite(e.Exception, "UnobservedTaskException");
        // 后台任务的异常不应让进程终止，记录后标记为已观察。
        e.SetObserved();
    }

    internal static void ConfigureServices(
        IServiceCollection services,
        ApplicationStoragePaths? storagePaths = null)
    {
        storagePaths ??= ApplicationStoragePathResolver.Resolve();
        services.AddSingleton<CoreContracts.ICacheManager, CacheManager>();
        services.AddSingleton<CoreContracts.ICacheStorageStatisticsService,
            BiliBiliLocalCacheManager.Core.Infrastructure.Management.FileSystemCacheStorageStatisticsService>();
        services.AddSingleton<CoreContracts.ICacheTrashService, BiliBiliLocalCacheManager.Core.Infrastructure.Management.FileSystemCacheTrashService>();
        services.AddSingleton<PlaybackContracts.IPlaybackArtifactStore>(_ =>
            storagePaths.IsTestMode
                ? new BiliBiliLocalCacheManager.Playback.Infrastructure.Playback.PlaybackArtifactStore(
                    storagePaths.TranscodeCacheRoot ??
                    throw new InvalidOperationException("Test transcode cache root is missing."))
                : BiliBiliLocalCacheManager.Playback.Infrastructure.Playback.PlaybackArtifactStore.Shared);
        // 播放与导出共用同一个实例，避免各自持有一份布局处理器与物化器。
        services.AddSingleton<CachePlaybackService>(provider =>
            new CachePlaybackService(
                provider.GetRequiredService<PlaybackContracts.IPlaybackArtifactStore>()));
        services.AddSingleton<PlaybackContracts.ICachePlaybackService>(provider =>
            provider.GetRequiredService<CachePlaybackService>());
        services.AddSingleton<PlaybackContracts.ICachePlaybackMaterializationService>(provider =>
            provider.GetRequiredService<CachePlaybackService>());
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IHelpService, HelpService>();
        services.AddSingleton<IExplorerService, ExplorerService>();
        services.AddSingleton<IAppSettingsService>(_ =>
            storagePaths.IsTestMode
                ? new JsonAppSettingsService(
                    storagePaths.SettingsPath ??
                    throw new InvalidOperationException("Test settings path is missing."))
                : new JsonAppSettingsService());
        services.AddSingleton<IPlaybackProgressDialogService, PlaybackProgressDialogService>();
        services.AddSingleton<IStorageOverviewService, StorageOverviewService>();
        services.AddSingleton<IDiagnosticEventRecorder, InMemoryDiagnosticEventRecorder>();
        services.AddSingleton<ISensitiveDataRedactor, SensitiveDataRedactor>();
        services.AddSingleton<IDiagnosticReportService, DiagnosticReportService>();
        services.AddSingleton<IFileSaveDialogService, FileSaveDialogService>();
        services.AddSingleton<PlaybackContracts.IFfmpegDiagnosticsProvider,
            BiliBiliLocalCacheManager.Playback.Infrastructure.Playback.BundledFfmpegDiagnosticsProvider>();
        services.AddSingleton<PlaybackContracts.IFfmpegPrewarmService,
            BiliBiliLocalCacheManager.Playback.Infrastructure.Playback.BundledFfmpegPrewarmService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();
    }
}

/// <summary>
/// 崩溃报告落盘。刻意不依赖 DI 容器，因为容器本身可能就是崩溃原因。
/// </summary>
internal static class CrashReportWriter
{
    public static string? TryWrite(Exception exception, string source)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiliBiliLocalCacheManager",
                "CrashReports");
            Directory.CreateDirectory(directory);

            var fileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log";
            var path = Path.Combine(directory, fileName);

            var builder = new StringBuilder();
            builder.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"来源：{source}");
            builder.AppendLine($"版本：{ReadInformationalVersion()}");
            builder.AppendLine($"系统：{Environment.OSVersion} / .NET {Environment.Version}");
            builder.AppendLine();
            builder.AppendLine(exception.ToString());

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            TrimOldReports(directory);
            return path;
        }
        catch (Exception)
        {
            // 崩溃处理路径本身绝不能再抛异常。
            return null;
        }
    }

    private static string ReadInformationalVersion()
    {
        var attribute = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault();
        return attribute?.InformationalVersion ?? "unknown";
    }

    private static void TrimOldReports(string directory)
    {
        const int keep = 20;
        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles("crash-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception)
        {
        }
    }
}
