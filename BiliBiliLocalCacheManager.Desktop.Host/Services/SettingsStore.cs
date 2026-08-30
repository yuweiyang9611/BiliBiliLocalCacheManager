using System.Text.Json;
using System.Text.Json.Serialization;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Desktop.Host.Rpc;
using BiliBiliLocalCacheManager.Playback.Models;

namespace BiliBiliLocalCacheManager.Desktop.Host.Services;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions FileSerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _syncRoot = new();
    private readonly string _settingsPath;
    private SettingsState? _state;

    public SettingsStore(string? settingsPath = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliBiliLocalCacheManager",
            "settings.json"));
    }

    public string SettingsPath => _settingsPath;

    public SettingsState GetState()
    {
        lock (_syncRoot)
        {
            _state ??= LoadCore();
            return _state with { Settings = _state.Settings.Clone() };
        }
    }

    public SettingsState Update(JsonElement patch)
    {
        lock (_syncRoot)
        {
            _state ??= LoadCore();
            if (!_state.CanSave)
            {
                throw new RpcException(
                    "settings_read_only",
                    _state.Message ?? "The settings file cannot be safely overwritten.");
            }

            var updated = ApplyPatch(_state.Settings.Clone(), patch);
            Normalize(updated);
            Validate(updated);
            SaveCore(updated);
            _state = new SettingsState(
                updated,
                CanSave: true,
                DesktopSettings.CurrentSchemaVersion,
                Message: null);
            return _state with { Settings = updated.Clone() };
        }
    }

    private SettingsState LoadCore()
    {
        if (!File.Exists(_settingsPath))
        {
            return new SettingsState(
                new DesktopSettings(),
                CanSave: true,
                SourceSchemaVersion: null,
                Message: null);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_settingsPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return RecoverCorrupt("The settings root is not a JSON object.");
            }

            var sourceVersion = document.RootElement.OptionalInt32("SchemaVersion") ?? 0;
            if (sourceVersion > DesktopSettings.CurrentSchemaVersion)
            {
                var futureSettings = JsonSerializer.Deserialize<DesktopSettings>(
                                         document.RootElement.GetRawText(),
                                         FileSerializerOptions) ??
                                     new DesktopSettings();
                Normalize(futureSettings);
                return new SettingsState(
                    futureSettings,
                    CanSave: false,
                    sourceVersion,
                    $"Settings schema v{sourceVersion} is newer than supported schema " +
                    $"v{DesktopSettings.CurrentSchemaVersion}; writes are disabled.");
            }

            var settings = JsonSerializer.Deserialize<DesktopSettings>(
                               document.RootElement.GetRawText(),
                               FileSerializerOptions) ??
                           new DesktopSettings();
            if (sourceVersion == 1)
            {
                // Schema v1 always remembered the last root and scanned it implicitly.
                // Preserve the root for an explicit UI migration decision, but make the
                // new startup behavior opt-in.
                settings.RememberRootPath = true;
                settings.ScanOnStartup = false;
            }

            Normalize(settings);
            Validate(settings);
            settings.SchemaVersion = DesktopSettings.CurrentSchemaVersion;
            return new SettingsState(
                settings,
                CanSave: true,
                sourceVersion,
                sourceVersion < DesktopSettings.CurrentSchemaVersion
                    ? $"Settings were migrated from schema v{sourceVersion}."
                    : null);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or RpcException)
        {
            return RecoverCorrupt(exception.Message);
        }
    }

    private SettingsState RecoverCorrupt(string reason)
    {
        var backupPath = _settingsPath + $".{DateTime.UtcNow:yyyyMMddHHmmssfff}.corrupt";
        try
        {
            File.Copy(_settingsPath, backupPath, overwrite: false);
            return new SettingsState(
                new DesktopSettings(),
                CanSave: true,
                SourceSchemaVersion: null,
                $"Invalid settings were backed up as {Path.GetFileName(backupPath)}: {reason}");
        }
        catch (Exception backupException) when (
            backupException is IOException or UnauthorizedAccessException)
        {
            return new SettingsState(
                new DesktopSettings(),
                CanSave: false,
                SourceSchemaVersion: null,
                $"Invalid settings could not be backed up; writes are disabled: {backupException.Message}");
        }
    }

    private void SaveCore(DesktopSettings settings)
    {
        settings.SchemaVersion = DesktopSettings.CurrentSchemaVersion;
        var directory = Path.GetDirectoryName(_settingsPath) ??
                        throw new IOException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.writing");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, FileSerializerOptions);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static DesktopSettings ApplyPatch(DesktopSettings settings, JsonElement patch)
    {
        if (patch.TryGetPropertyIgnoreCase("settings", out var nested) ||
            patch.TryGetPropertyIgnoreCase("patch", out nested))
        {
            if (nested.ValueKind != JsonValueKind.Object)
            {
                throw new RpcException("invalid_params", "Property 'settings' must be an object.");
            }

            patch = nested;
        }

        settings.RootPath = patch.OptionalString("rootPath") ?? settings.RootPath;
        settings.RememberRootPath =
            patch.OptionalBoolean("rememberRootPath") ?? settings.RememberRootPath;
        settings.ScanOnStartup =
            patch.OptionalBoolean("scanOnStartup") ?? settings.ScanOnStartup;
        settings.IncludeIncomplete = patch.OptionalBoolean("includeIncomplete") ?? settings.IncludeIncomplete;
        if (patch.TryGetPropertyIgnoreCase("keyword", out var keywordElement))
        {
            if (keywordElement.ValueKind != JsonValueKind.String)
            {
                throw new RpcException("invalid_params", "Property 'keyword' must be a string.");
            }

            settings.Keyword = keywordElement.GetString() ?? string.Empty;
        }
        settings.SplitKeywords = patch.OptionalBoolean("splitKeywords") ?? settings.SplitKeywords;
        settings.AnyKeywords = patch.OptionalBoolean("anyKeywords") ?? settings.AnyKeywords;
        settings.IncludePartName = patch.OptionalBoolean("includePartName") ?? settings.IncludePartName;
        settings.IncludeOwnerName = patch.OptionalBoolean("includeOwnerName") ?? settings.IncludeOwnerName;
        settings.IncludeBvid = patch.OptionalBoolean("includeBvid") ?? settings.IncludeBvid;
        settings.IncludeAvid = patch.OptionalBoolean("includeAvid") ?? settings.IncludeAvid;
        settings.CaseSensitive = patch.OptionalBoolean("caseSensitive") ?? settings.CaseSensitive;
        settings.MatchMode = ParseMatchMode(
            patch.OptionalString("matchMode"),
            settings.MatchMode);
        settings.PreferredPlayer = ParsePlayerPreference(
            patch.OptionalString("playerPreference") ?? patch.OptionalString("preferredPlayer"),
            settings.PreferredPlayer);
        settings.TranscodeCacheRetentionDays =
            patch.OptionalInt32("transcodeCacheRetentionDays") ?? settings.TranscodeCacheRetentionDays;
        settings.TranscodeCacheMaxSizeGigabytes =
            patch.OptionalInt32("transcodeCacheMaxSizeGigabytes") ?? settings.TranscodeCacheMaxSizeGigabytes;
        return settings;
    }

    private static void Normalize(DesktopSettings settings)
    {
        settings.RootPath = settings.RootPath?.Trim() ?? string.Empty;
        settings.Keyword ??= string.Empty;
        if (!settings.RememberRootPath)
        {
            settings.RootPath = string.Empty;
            settings.ScanOnStartup = false;
        }
    }

    private static CacheSearchMatchMode ParseMatchMode(
        string? value,
        CacheSearchMatchMode defaultValue)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultValue,
            "contains" => CacheSearchMatchMode.Contains,
            "prefix" or "startswith" => CacheSearchMatchMode.StartsWith,
            "exact" or "equals" => CacheSearchMatchMode.Equals,
            _ => throw new RpcException("invalid_params", $"Unsupported matchMode '{value}'.")
        };
    }

    private static PlaybackPlayerPreference ParsePlayerPreference(
        string? value,
        PlaybackPlayerPreference defaultValue)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => defaultValue,
            "system" or "systemdefaultfirst" => PlaybackPlayerPreference.SystemDefaultFirst,
            "systemdefaultonly" => PlaybackPlayerPreference.SystemDefaultOnly,
            "mpv" => PlaybackPlayerPreference.Mpv,
            "vlc" => PlaybackPlayerPreference.Vlc,
            _ => throw new RpcException("invalid_params", $"Unsupported playerPreference '{value}'.")
        };
    }

    private static void Validate(DesktopSettings settings)
    {
        if (!settings.RememberRootPath &&
            (!string.IsNullOrEmpty(settings.RootPath) || settings.ScanOnStartup))
        {
            throw new RpcException(
                "invalid_params",
                "rootPath and scanOnStartup must be disabled when rememberRootPath is false.");
        }

        if (!Enum.IsDefined(settings.MatchMode))
        {
            throw new RpcException("invalid_params", "The configured search match mode is invalid.");
        }

        if (!Enum.IsDefined(settings.PreferredPlayer))
        {
            throw new RpcException("invalid_params", "The configured playback player is invalid.");
        }

        if (!PlaybackArtifactCleanupOptions.IsValidRetentionDays(settings.TranscodeCacheRetentionDays))
        {
            throw new RpcException(
                "invalid_params",
                $"transcodeCacheRetentionDays must be between " +
                $"{PlaybackArtifactCleanupOptions.MinimumRetentionDays} and " +
                $"{PlaybackArtifactCleanupOptions.MaximumRetentionDays}.");
        }

        if (!PlaybackArtifactCleanupOptions.IsValidMaxSizeGigabytes(settings.TranscodeCacheMaxSizeGigabytes))
        {
            throw new RpcException(
                "invalid_params",
                $"transcodeCacheMaxSizeGigabytes must be between " +
                $"{PlaybackArtifactCleanupOptions.MinimumMaxSizeGigabytes} and " +
                $"{PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
