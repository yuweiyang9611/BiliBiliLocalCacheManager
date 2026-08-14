using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiliBiliLocalCacheManager.Core.Application.Models;
using BiliBiliLocalCacheManager.Playback.Models;
using BiliBiliLocalCacheManager.Wpf.Models;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private static readonly TimeSpan SettingsMutexTimeout = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly string _settingsPath;
    private readonly string _settingsMutexName;
    private readonly Action<int>? _beforeReplaceAttemptForTesting;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private string? _authorizedCorruptTargetHash;
    private bool _hasWriteBaseline;
    private bool _expectedTargetMissing;
    private string? _expectedCompatibleTargetHash;
    private string? _saveBlockedReason;

    public JsonAppSettingsService(string? settingsPath = null)
        : this(settingsPath, beforeReplaceAttemptForTesting: null)
    {
    }

    internal JsonAppSettingsService(
        string? settingsPath,
        Action<int>? beforeReplaceAttemptForTesting)
    {
        _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliBiliLocalCacheManager",
            "settings.json"));
        _settingsMutexName = CreateSettingsMutexName(_settingsPath);
        _beforeReplaceAttemptForTesting = beforeReplaceAttemptForTesting;
    }

    public AppSettings Load()
    {
        return LoadWithReport().Settings;
    }

    public AppSettingsLoadResult LoadWithReport()
    {
        lock (_sync)
        {
            using var settingsMutex = EnterSettingsMutex();
            return LoadWithReportCore();
        }
    }

    public AutomaticMaintenanceEligibility CheckAutomaticMaintenanceEligibility()
    {
        lock (_sync)
        {
            try
            {
                using var settingsMutex = EnterSettingsMutex();
                return CheckAutomaticMaintenanceEligibilityCore();
            }
            catch (Exception ex) when (
                IsSettingsIoFailure(ex) || ex is TimeoutException)
            {
                return new AutomaticMaintenanceEligibility(
                    IsEligible: false,
                    AppSettingsLoadKind.ReadError,
                    SourceSchemaVersion: null,
                    $"无法只读复核设置文件：{ex.Message}");
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_saveBlockedReason))
            {
                throw new InvalidOperationException(_saveBlockedReason);
            }

            ValidateForSave(settings);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            using var settingsMutex = EnterSettingsMutex();
            var directory = Path.GetDirectoryName(_settingsPath)
                            ?? throw new InvalidOperationException("设置文件路径缺少父目录。");
            Directory.CreateDirectory(directory);
            EnsureCurrentTargetCanBeOverwritten();

            var temporaryPath = CreateUniqueTemporaryPath();
            try
            {
                var serializedContents = JsonSerializer.SerializeToUtf8Bytes(
                    settings,
                    _jsonOptions);
                File.WriteAllBytes(temporaryPath, serializedContents);
                ReplaceSettingsFileWithRetry(temporaryPath);
                _authorizedCorruptTargetHash = null;
                SetCompatibleWriteBaseline(serializedContents);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // 下次保存会覆盖该临时文件。
                }
            }
        }
    }

    private AppSettingsLoadResult LoadWithReportCore()
    {
        byte[] contents;
        try
        {
            contents = File.ReadAllBytes(_settingsPath);
        }
        catch (FileNotFoundException)
        {
            _authorizedCorruptTargetHash = null;
            _saveBlockedReason = null;
            SetMissingWriteBaseline();
            return AppSettingsLoadResult.Missing(new AppSettings());
        }
        catch (DirectoryNotFoundException)
        {
            _authorizedCorruptTargetHash = null;
            _saveBlockedReason = null;
            SetMissingWriteBaseline();
            return AppSettingsLoadResult.Missing(new AppSettings());
        }
        catch (Exception ex) when (IsSettingsIoFailure(ex))
        {
            _authorizedCorruptTargetHash = null;
            ClearWriteBaseline();
            _saveBlockedReason = $"无法读取现有设置文件，已禁止覆盖：{ex.Message}";
            return new AppSettingsLoadResult(
                AppSettingsLoadKind.ReadError,
                new AppSettings(),
                SourceSchemaVersion: null,
                RequiresSave: false,
                IsUnsupported: false,
                CanSave: false,
                CanRunAutomaticMaintenance: false,
                Array.Empty<string>(),
                $"读取设置文件失败：{ex.Message}；本次启动已停用自动缓存维护。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(contents);
        }
        catch (JsonException ex)
        {
            return HandleCorruptSettings(
                contents,
                $"JSON 格式无效：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return HandleCorruptSettings(
                    contents,
                    "设置文件根节点不是 JSON 对象");
            }

            if (!TryReadSchemaVersion(root, out var sourceSchemaVersion))
            {
                return HandleCorruptSettings(
                    contents,
                    "SchemaVersion 必须是非负整数");
            }

            if (sourceSchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                _authorizedCorruptTargetHash = null;
                ClearWriteBaseline();
                _saveBlockedReason =
                    $"设置文件版本 v{sourceSchemaVersion} 高于当前支持的 " +
                    $"v{AppSettings.CurrentSchemaVersion}，已禁止覆盖。";
                return new AppSettingsLoadResult(
                    AppSettingsLoadKind.FutureVersion,
                    new AppSettings(),
                    sourceSchemaVersion,
                    RequiresSave: false,
                    IsUnsupported: true,
                    CanSave: false,
                    CanRunAutomaticMaintenance: false,
                    Array.Empty<string>(),
                    $"设置文件来自较新版本 v{sourceSchemaVersion}，本版本不会覆盖；" +
                    "本次启动已停用自动缓存维护。");
            }

            var adjustments = new List<string>();
            var settings = ReadKnownSettings(root, adjustments);
            if (sourceSchemaVersion == 0)
            {
                adjustments.Insert(
                    0,
                    $"设置格式已从版本 0 升级到版本 {AppSettings.CurrentSchemaVersion}");
            }

            var requiresSave =
                sourceSchemaVersion < AppSettings.CurrentSchemaVersion ||
                adjustments.Count > 0;
            _authorizedCorruptTargetHash = null;
            SetCompatibleWriteBaseline(contents);
            _saveBlockedReason = null;
            return new AppSettingsLoadResult(
                sourceSchemaVersion == 0
                    ? AppSettingsLoadKind.LegacyVersion
                    : AppSettingsLoadKind.CurrentVersion,
                settings,
                sourceSchemaVersion,
                requiresSave,
                IsUnsupported: false,
                CanSave: true,
                CanRunAutomaticMaintenance: true,
                adjustments.ToArray(),
                requiresSave
                    ? $"旧值已调整：{string.Join("；", adjustments)}。"
                    : null);
        }
    }

    private AutomaticMaintenanceEligibility CheckAutomaticMaintenanceEligibilityCore()
    {
        byte[] contents;
        try
        {
            contents = File.ReadAllBytes(_settingsPath);
        }
        catch (FileNotFoundException)
        {
            if (_hasWriteBaseline && !_expectedTargetMissing)
            {
                return CreateAutomaticMaintenanceConflict(
                    "设置文件在本次启动后被删除");
            }

            return new AutomaticMaintenanceEligibility(
                IsEligible: true,
                AppSettingsLoadKind.MissingFile,
                SourceSchemaVersion: null,
                Reason: null,
                Settings: new AppSettings());
        }
        catch (DirectoryNotFoundException)
        {
            if (_hasWriteBaseline && !_expectedTargetMissing)
            {
                return CreateAutomaticMaintenanceConflict(
                    "设置文件在本次启动后被删除");
            }

            return new AutomaticMaintenanceEligibility(
                IsEligible: true,
                AppSettingsLoadKind.MissingFile,
                SourceSchemaVersion: null,
                Reason: null,
                Settings: new AppSettings());
        }
        catch (Exception ex) when (IsSettingsIoFailure(ex))
        {
            return new AutomaticMaintenanceEligibility(
                IsEligible: false,
                AppSettingsLoadKind.ReadError,
                SourceSchemaVersion: null,
                $"读取设置文件失败：{ex.Message}");
        }

        if (_hasWriteBaseline)
        {
            if (_expectedTargetMissing)
            {
                return CreateAutomaticMaintenanceConflict(
                    "设置文件在本次启动后由其他实例创建");
            }

            var currentHash = ComputeContentHash(contents);
            if (!string.Equals(
                    currentHash,
                    _expectedCompatibleTargetHash,
                    StringComparison.Ordinal))
            {
                return CreateAutomaticMaintenanceConflict(
                    "设置文件已被其他实例更改");
            }
        }

        var targetKind = InspectTarget(contents, out var schemaVersion);
        return targetKind switch
        {
            SettingsTargetKind.Compatible =>
                CreateCompatibleAutomaticMaintenanceEligibility(
                    contents,
                    schemaVersion),
            SettingsTargetKind.FutureVersion => new AutomaticMaintenanceEligibility(
                IsEligible: false,
                AppSettingsLoadKind.FutureVersion,
                schemaVersion,
                $"设置文件版本 v{schemaVersion} 高于当前支持的 " +
                $"v{AppSettings.CurrentSchemaVersion}"),
            _ => new AutomaticMaintenanceEligibility(
                IsEligible: false,
                AppSettingsLoadKind.CorruptFile,
                SourceSchemaVersion: null,
                "设置文件格式已损坏或 SchemaVersion 无效")
        };
    }

    private AutomaticMaintenanceEligibility CreateCompatibleAutomaticMaintenanceEligibility(
        byte[] contents,
        int? schemaVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(contents);
            var adjustments = new List<string>();
            var settings = ReadKnownSettings(document.RootElement, adjustments);
            if (adjustments.Count > 0)
            {
                return new AutomaticMaintenanceEligibility(
                    IsEligible: false,
                    AppSettingsLoadKind.CorruptFile,
                    schemaVersion,
                    $"设置值无法安全用于自动维护：{string.Join("；", adjustments)}");
            }

            return new AutomaticMaintenanceEligibility(
                IsEligible: true,
                schemaVersion == 0
                    ? AppSettingsLoadKind.LegacyVersion
                    : AppSettingsLoadKind.CurrentVersion,
                schemaVersion,
                Reason: null,
                Settings: settings);
        }
        catch (JsonException ex)
        {
            return new AutomaticMaintenanceEligibility(
                IsEligible: false,
                AppSettingsLoadKind.CorruptFile,
                SourceSchemaVersion: null,
                $"设置文件格式无效：{ex.Message}");
        }
    }

    private static AutomaticMaintenanceEligibility CreateAutomaticMaintenanceConflict(
        string reason)
    {
        return new AutomaticMaintenanceEligibility(
            IsEligible: false,
            AppSettingsLoadKind.ReadError,
            SourceSchemaVersion: null,
            $"{reason}，请重启应用后再执行自动维护。");
    }

    private AppSettingsLoadResult HandleCorruptSettings(
        byte[] originalContents,
        string reason)
    {
        ClearWriteBaseline();
        var adjustment = "设置文件损坏，已恢复默认设置";
        if (TryBackupCorruptSettings(
                originalContents,
                out var backupPath,
                out var backupError))
        {
            _authorizedCorruptTargetHash = ComputeContentHash(originalContents);
            _saveBlockedReason = null;
            return new AppSettingsLoadResult(
                AppSettingsLoadKind.CorruptFile,
                new AppSettings(),
                SourceSchemaVersion: null,
                RequiresSave: true,
                IsUnsupported: false,
                CanSave: true,
                CanRunAutomaticMaintenance: false,
                new[] { adjustment },
                $"设置文件损坏（{reason}），已备份为 {Path.GetFileName(backupPath)}；" +
                "本次启动已停用自动缓存维护。");
        }

        _authorizedCorruptTargetHash = null;
        _saveBlockedReason =
            $"设置文件损坏且无法创建备份，已禁止覆盖：{backupError}";
        return new AppSettingsLoadResult(
            AppSettingsLoadKind.CorruptFile,
            new AppSettings(),
            SourceSchemaVersion: null,
            RequiresSave: false,
            IsUnsupported: false,
            CanSave: false,
            CanRunAutomaticMaintenance: false,
            new[] { adjustment },
            $"设置文件损坏（{reason}），且备份失败：{backupError}；" +
            "本次启动已停用自动缓存维护。");
    }

    private bool TryBackupCorruptSettings(
        byte[] originalContents,
        out string? backupPath,
        out string? errorMessage)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        var fileName = Path.GetFileNameWithoutExtension(_settingsPath);
        var extension = Path.GetExtension(_settingsPath);
        var timestamp = DateTime.UtcNow.ToString(
            "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture);

        for (var suffix = 0; suffix < 100; suffix++)
        {
            var suffixText = suffix == 0 ? string.Empty : $"-{suffix}";
            var candidate = Path.Combine(
                directory,
                $"{fileName}.{timestamp}{suffixText}.corrupt{extension}");
            if (File.Exists(candidate))
            {
                continue;
            }

            try
            {
                using (var backup = new FileStream(
                           candidate,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    backup.Write(originalContents);
                    backup.Flush(flushToDisk: true);
                }
                backupPath = candidate;
                errorMessage = null;
                return true;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // A concurrent backup used this name; try the next suffix.
            }
            catch (Exception ex) when (IsSettingsIoFailure(ex))
            {
                backupPath = null;
                errorMessage = ex.Message;
                return false;
            }
        }

        backupPath = null;
        errorMessage = "无法为损坏的设置文件分配唯一备份名称。";
        return false;
    }

    private static AppSettings ReadKnownSettings(
        JsonElement root,
        List<string> adjustments)
    {
        var defaults = new AppSettings();
        return new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            RootPath = ReadString(
                root,
                nameof(AppSettings.RootPath),
                defaults.RootPath,
                "缓存根目录",
                adjustments),
            IncludeIncomplete = ReadBoolean(
                root,
                nameof(AppSettings.IncludeIncomplete),
                defaults.IncludeIncomplete,
                "包含未完成缓存",
                adjustments),
            SplitKeywords = ReadBoolean(
                root,
                nameof(AppSettings.SplitKeywords),
                defaults.SplitKeywords,
                "拆分关键词",
                adjustments),
            AnyKeywords = ReadBoolean(
                root,
                nameof(AppSettings.AnyKeywords),
                defaults.AnyKeywords,
                "任一关键词匹配",
                adjustments),
            IncludePartName = ReadBoolean(
                root,
                nameof(AppSettings.IncludePartName),
                defaults.IncludePartName,
                "搜索分集名称",
                adjustments),
            IncludeOwnerName = ReadBoolean(
                root,
                nameof(AppSettings.IncludeOwnerName),
                defaults.IncludeOwnerName,
                "搜索 UP 主名称",
                adjustments),
            IncludeBvid = ReadBoolean(
                root,
                nameof(AppSettings.IncludeBvid),
                defaults.IncludeBvid,
                "搜索 BVID",
                adjustments),
            IncludeAvid = ReadBoolean(
                root,
                nameof(AppSettings.IncludeAvid),
                defaults.IncludeAvid,
                "搜索 AVID",
                adjustments),
            CaseSensitive = ReadBoolean(
                root,
                nameof(AppSettings.CaseSensitive),
                defaults.CaseSensitive,
                "区分大小写",
                adjustments),
            MatchMode = ReadEnum(
                root,
                nameof(AppSettings.MatchMode),
                defaults.MatchMode,
                "搜索匹配模式",
                adjustments),
            PreferredPlayer = ReadEnum(
                root,
                nameof(AppSettings.PreferredPlayer),
                defaults.PreferredPlayer,
                "首选播放器",
                adjustments),
            TranscodeCacheRetentionDays = ReadPolicyLimit(
                root,
                nameof(AppSettings.TranscodeCacheRetentionDays),
                PlaybackArtifactCleanupOptions.DefaultRetentionDays,
                PlaybackArtifactCleanupOptions.MinimumRetentionDays,
                PlaybackArtifactCleanupOptions.MaximumRetentionDays,
                "转码缓存保留天数",
                adjustments),
            TranscodeCacheMaxSizeGigabytes = ReadPolicyLimit(
                root,
                nameof(AppSettings.TranscodeCacheMaxSizeGigabytes),
                PlaybackArtifactCleanupOptions.DefaultMaxSizeGigabytes,
                PlaybackArtifactCleanupOptions.MinimumMaxSizeGigabytes,
                PlaybackArtifactCleanupOptions.MaximumMaxSizeGigabytes,
                "转码缓存容量上限",
                adjustments)
        };
    }

    private static string ReadString(
        JsonElement root,
        string propertyName,
        string defaultValue,
        string displayName,
        List<string> adjustments)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            return text;
        }

        adjustments.Add($"{displayName}无法识别，已恢复默认值");
        return defaultValue;
    }

    private static bool ReadBoolean(
        JsonElement root,
        string propertyName,
        bool defaultValue,
        string displayName,
        List<string> adjustments)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        adjustments.Add($"{displayName}无法识别，已恢复默认值");
        return defaultValue;
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement root,
        string propertyName,
        TEnum defaultValue,
        string displayName,
        List<string> adjustments)
        where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<TEnum>(value.GetString(), ignoreCase: true, out var parsed) &&
            Enum.IsDefined(typeof(TEnum), parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var numericValue) &&
            Enum.IsDefined(typeof(TEnum), numericValue))
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), numericValue);
        }

        adjustments.Add($"{displayName}无法识别，已恢复默认值");
        return defaultValue;
    }

    private static int ReadPolicyLimit(
        JsonElement root,
        string propertyName,
        int defaultValue,
        int minimumValue,
        int maximumValue,
        string displayName,
        List<string> adjustments)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var numericValue))
        {
            adjustments.Add($"{displayName}无法识别，已恢复为 {defaultValue}");
            return defaultValue;
        }

        if (numericValue < minimumValue)
        {
            adjustments.Add(
                $"{displayName}已从 {numericValue} 恢复为默认值 {defaultValue}");
            return defaultValue;
        }

        if (numericValue > maximumValue)
        {
            adjustments.Add(
                $"{displayName}已从 {numericValue} 调整为 {maximumValue}");
            return maximumValue;
        }

        return numericValue;
    }

    private static bool TryReadSchemaVersion(
        JsonElement root,
        out int schemaVersion)
    {
        if (!root.TryGetProperty(nameof(AppSettings.SchemaVersion), out var value))
        {
            schemaVersion = 0;
            return true;
        }

        schemaVersion = -1;
        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out schemaVersion) &&
            schemaVersion >= 0;
    }

    private void EnsureCurrentTargetCanBeOverwritten()
    {
        byte[] currentContents;
        try
        {
            currentContents = File.ReadAllBytes(_settingsPath);
        }
        catch (FileNotFoundException)
        {
            if (_authorizedCorruptTargetHash is not null)
            {
                throw new InvalidOperationException(
                    "设置文件在损坏备份后已被删除，已拒绝覆盖其他实例的更改。");
            }

            EnsureMissingTargetMatchesWriteBaseline();
            return;
        }
        catch (DirectoryNotFoundException)
        {
            if (_authorizedCorruptTargetHash is not null)
            {
                throw new InvalidOperationException(
                    "设置文件在损坏备份后已被删除，已拒绝覆盖其他实例的更改。");
            }

            EnsureMissingTargetMatchesWriteBaseline();
            return;
        }
        catch (Exception ex) when (IsSettingsIoFailure(ex))
        {
            throw new InvalidOperationException(
                $"无法重新读取现有设置文件，已拒绝覆盖：{ex.Message}",
                ex);
        }

        var currentHash = ComputeContentHash(currentContents);
        if (_authorizedCorruptTargetHash is not null)
        {
            if (string.Equals(
                    currentHash,
                    _authorizedCorruptTargetHash,
                    StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                "设置文件在损坏备份后已发生变化，已拒绝覆盖其他实例的更改。");
        }

        var targetKind = InspectTarget(currentContents, out var schemaVersion);
        if (targetKind == SettingsTargetKind.FutureVersion)
        {
            throw new InvalidOperationException(
                $"现有设置文件版本 v{schemaVersion} 高于当前支持的 " +
                $"v{AppSettings.CurrentSchemaVersion}，已拒绝覆盖。");
        }

        if (targetKind == SettingsTargetKind.Corrupt)
        {
            throw new InvalidOperationException(
                "现有设置文件已损坏且未由本服务实例成功备份，已拒绝覆盖。");
        }

        if (_hasWriteBaseline)
        {
            if (_expectedTargetMissing)
            {
                throw new InvalidOperationException(
                    "设置文件在加载后已由其他实例创建，已拒绝覆盖其更改。");
            }

            if (!string.Equals(
                    currentHash,
                    _expectedCompatibleTargetHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "设置文件已被其他实例更改，已拒绝用当前会话中的旧设置覆盖；" +
                    "请重启应用后重试。");
            }
        }
        else
        {
            SetCompatibleWriteBaseline(currentContents);
        }
    }

    private void EnsureMissingTargetMatchesWriteBaseline()
    {
        if (_hasWriteBaseline && !_expectedTargetMissing)
        {
            throw new InvalidOperationException(
                "设置文件在加载后已被删除，已拒绝用当前会话中的旧设置重新创建；" +
                "请重启应用后重试。");
        }

        if (!_hasWriteBaseline)
        {
            SetMissingWriteBaseline();
        }
    }

    private void SetMissingWriteBaseline()
    {
        _hasWriteBaseline = true;
        _expectedTargetMissing = true;
        _expectedCompatibleTargetHash = null;
    }

    private void SetCompatibleWriteBaseline(byte[] contents)
    {
        _hasWriteBaseline = true;
        _expectedTargetMissing = false;
        _expectedCompatibleTargetHash = ComputeContentHash(contents);
    }

    private void ClearWriteBaseline()
    {
        _hasWriteBaseline = false;
        _expectedTargetMissing = false;
        _expectedCompatibleTargetHash = null;
    }

    private static SettingsTargetKind InspectTarget(
        byte[] contents,
        out int? schemaVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(contents);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadSchemaVersion(root, out var parsedSchemaVersion))
            {
                schemaVersion = null;
                return SettingsTargetKind.Corrupt;
            }

            schemaVersion = parsedSchemaVersion;
            return parsedSchemaVersion > AppSettings.CurrentSchemaVersion
                ? SettingsTargetKind.FutureVersion
                : SettingsTargetKind.Compatible;
        }
        catch (JsonException)
        {
            schemaVersion = null;
            return SettingsTargetKind.Corrupt;
        }
    }

    private static string ComputeContentHash(byte[] contents)
    {
        return Convert.ToHexString(SHA256.HashData(contents));
    }

    private static string CreateSettingsMutexName(string settingsPath)
    {
        var canonicalPath = Path.GetFullPath(settingsPath);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        var suffix = Convert.ToHexString(hash.AsSpan(0, 16));
        return OperatingSystem.IsWindows()
            ? $@"Local\BiliBiliLocalCacheManager.Settings.{suffix}"
            : $"BiliBiliLocalCacheManager.Settings.{suffix}";
    }

    private IDisposable EnterSettingsMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _settingsMutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(SettingsMutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException(
                    $"等待其他应用实例完成设置文件操作超时：{_settingsPath}");
            }

            return new MutexReleaser(mutex);
        }
        catch
        {
            if (!acquired)
            {
                mutex.Dispose();
            }

            throw;
        }
    }

    private string CreateUniqueTemporaryPath()
    {
        return $"{_settingsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.writing";
    }

    private void ReplaceSettingsFileWithRetry(string temporaryPath)
    {
        const int maximumAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            // Revalidate immediately before every replace attempt, including
            // the first one. The target can change while the temporary file is
            // being serialized, and an older version or another writer may not
            // coordinate through this version's named mutex.
            EnsureCurrentTargetCanBeOverwritten();

            try
            {
                _beforeReplaceAttemptForTesting?.Invoke(attempt);
                File.Move(temporaryPath, _settingsPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                IsSettingsIoFailure(ex) &&
                attempt < maximumAttempts)
            {
                // Virus scanners and indexers can briefly open the destination
                // between the guarded read and the atomic rename on Windows.
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }

    private static void ValidateForSave(AppSettings settings)
    {
        _ = settings.CreateTranscodeCacheCleanupOptions();
        if (!Enum.IsDefined(typeof(CacheSearchMatchMode), settings.MatchMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "搜索匹配模式无效。");
        }

        if (!Enum.IsDefined(typeof(PlaybackPlayerPreference), settings.PreferredPlayer))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "首选播放器设置无效。");
        }
    }

    private static bool IsSettingsIoFailure(Exception exception)
    {
        return exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException;
    }

    private enum SettingsTargetKind
    {
        Compatible,
        FutureVersion,
        Corrupt
    }

    private sealed class MutexReleaser(Mutex mutex) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            finally
            {
                mutex.Dispose();
            }
        }
    }
}
