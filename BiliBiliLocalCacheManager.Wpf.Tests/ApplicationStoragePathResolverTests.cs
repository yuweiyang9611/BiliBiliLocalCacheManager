using System.IO;
using BiliBiliLocalCacheManager.Playback.Contracts;
using BiliBiliLocalCacheManager.Playback.Infrastructure.Playback;
using BiliBiliLocalCacheManager.Wpf.Models;
using BiliBiliLocalCacheManager.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BiliBiliLocalCacheManager.Wpf.Tests;

public sealed class ApplicationStoragePathResolverTests
{
    [Fact]
    public void Resolve_WithoutExplicitTestMode_IgnoresAllPathOverrides()
    {
        var requestedVariables = new List<string>();

        var result = ApplicationStoragePathResolver.Resolve(name =>
        {
            requestedVariables.Add(name);
            return name == ApplicationStoragePathResolver.TestModeEnvironmentVariable
                ? "0"
                : throw new InvalidOperationException(
                    "Production mode must not inspect test-only path overrides.");
        });

        Assert.Same(ApplicationStoragePaths.Production, result);
        Assert.False(result.IsTestMode);
        Assert.Null(result.SettingsPath);
        Assert.Null(result.TranscodeCacheRoot);
        Assert.Equal(
            [ApplicationStoragePathResolver.TestModeEnvironmentVariable],
            requestedVariables);

        var services = new ServiceCollection();
        App.ConfigureServices(services, result);
        using var provider = services.BuildServiceProvider();
        Assert.Same(
            PlaybackArtifactStore.Shared,
            provider.GetRequiredService<IPlaybackArtifactStore>());
        Assert.IsType<JsonAppSettingsService>(
            provider.GetRequiredService<IAppSettingsService>());
    }

    [Fact]
    public void Resolve_InTestMode_UsesIsolatedSettingsAndTranscodePathsInDi()
    {
        using var workspace = new TemporaryDirectory();
        var settingsPath = Path.Combine(
            workspace.Path,
            "settings",
            "isolated-settings.json");
        var transcodeRoot = Path.Combine(workspace.Path, "isolated-transcode");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ApplicationStoragePathResolver.TestModeEnvironmentVariable] = "1",
            [ApplicationStoragePathResolver.SettingsPathEnvironmentVariable] = settingsPath,
            [ApplicationStoragePathResolver.TranscodeCacheRootEnvironmentVariable] = transcodeRoot
        };

        var result = ApplicationStoragePathResolver.Resolve(name =>
            values.GetValueOrDefault(name));

        Assert.True(result.IsTestMode);
        Assert.Equal(Path.GetFullPath(settingsPath), result.SettingsPath);
        Assert.Equal(Path.GetFullPath(transcodeRoot), result.TranscodeCacheRoot);

        var services = new ServiceCollection();
        App.ConfigureServices(services, result);
        using var provider = services.BuildServiceProvider();
        var artifactStore = provider.GetRequiredService<IPlaybackArtifactStore>();
        var settingsService = provider.GetRequiredService<IAppSettingsService>();
        Assert.NotSame(PlaybackArtifactStore.Shared, artifactStore);
        Assert.Equal(Path.GetFullPath(transcodeRoot), artifactStore.RootDirectory);

        settingsService.Save(new AppSettings { RootPath = @"D:\IsolatedCache" });

        Assert.True(File.Exists(settingsPath));
        Assert.Equal(
            @"D:\IsolatedCache",
            new JsonAppSettingsService(settingsPath).Load().RootPath);
    }

    [Theory]
    [InlineData(null, @"D:\Transcode")]
    [InlineData(@"D:\settings.json", null)]
    public void Resolve_InTestMode_RequiresBothIsolationPaths(
        string? settingsPath,
        string? transcodeRoot)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ApplicationStoragePathResolver.TestModeEnvironmentVariable] = "1",
            [ApplicationStoragePathResolver.SettingsPathEnvironmentVariable] = settingsPath,
            [ApplicationStoragePathResolver.TranscodeCacheRootEnvironmentVariable] = transcodeRoot
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ApplicationStoragePathResolver.Resolve(name =>
                values.GetValueOrDefault(name)));

        Assert.Contains(
            ApplicationStoragePathResolver.SettingsPathEnvironmentVariable,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            ApplicationStoragePathResolver.TranscodeCacheRootEnvironmentVariable,
            exception.Message,
            StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"BiliBiliLocalCacheManager.StorageResolverTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
