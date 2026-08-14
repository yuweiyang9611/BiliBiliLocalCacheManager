using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
// 项目同时启用了 WinForms，需显式指定使用 WPF 的 Color。
using Color = System.Windows.Media.Color;

namespace BiliBiliLocalCacheManager.Wpf.Services;

public enum AppThemeVariant
{
    Light,
    Dark
}

/// <summary>
/// 应用自定义的语义画刷。WPF 的 Fluent 主题只负责内置控件外观，
/// 界面上的卡片描边、次要文字、状态色仍需要自己随主题切换，否则深色模式下会不可读。
/// </summary>
public static class AppThemePalette
{
    public const string CardBorderBrushKey = "CardBorderBrush";
    public const string SecondaryTextBrushKey = "SecondaryTextBrush";
    public const string TertiaryTextBrushKey = "TertiaryTextBrush";
    public const string StatusNormalBrushKey = "StatusNormalBrush";
    public const string StatusErrorBrushKey = "StatusErrorBrush";
    public const string AlternatingRowBrushKey = "AlternatingRowBrush";

    /// <summary>
    /// 单元测试没有 Application 实例时 ViewModel 使用的回退色，与浅色主题保持一致。
    /// </summary>
    public static Color FallbackStatusNormal => Color.FromRgb(102, 102, 102);

    public static Color FallbackStatusError => Color.FromRgb(192, 0, 0);

    public static IReadOnlyDictionary<string, Color> For(AppThemeVariant variant)
    {
        return variant == AppThemeVariant.Dark
            ? new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                [CardBorderBrushKey] = Color.FromRgb(0x3A, 0x3A, 0x3D),
                [SecondaryTextBrushKey] = Color.FromRgb(0xC5, 0xC5, 0xC8),
                [TertiaryTextBrushKey] = Color.FromRgb(0x9A, 0x9A, 0x9E),
                [StatusNormalBrushKey] = Color.FromRgb(0xC5, 0xC5, 0xC8),
                // 深色底上的红色需要提亮，#C00000 在深色背景上几乎不可读。
                [StatusErrorBrushKey] = Color.FromRgb(0xFF, 0x6B, 0x6B),
                [AlternatingRowBrushKey] = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            }
            : new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                [CardBorderBrushKey] = Color.FromRgb(0xE0, 0xE0, 0xE0),
                [SecondaryTextBrushKey] = Color.FromRgb(0x55, 0x55, 0x55),
                [TertiaryTextBrushKey] = Color.FromRgb(0x66, 0x66, 0x66),
                [StatusNormalBrushKey] = FallbackStatusNormal,
                [StatusErrorBrushKey] = FallbackStatusError,
                [AlternatingRowBrushKey] = Color.FromRgb(0xF7, 0xF7, 0xF7)
            };
    }

    /// <summary>
    /// 读取 Windows 的“应用模式”设置。读取失败一律按浅色处理。
    /// </summary>
    public static AppThemeVariant DetectSystemVariant()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0 ? AppThemeVariant.Dark : AppThemeVariant.Light;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return AppThemeVariant.Light;
    }

    /// <summary>
    /// 把调色板写入资源字典。界面使用 DynamicResource 引用，因此主题切换会立即生效。
    /// </summary>
    public static void Apply(System.Windows.ResourceDictionary resources, AppThemeVariant variant)
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var (key, color) in For(variant))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }
}
