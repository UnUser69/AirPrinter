using System.Windows;
using MediaColor = System.Windows.Media.Color;
using Microsoft.Win32;

namespace AirPrinter;

internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Apply(System.Windows.Application application)
    {
        IsDark = ReadDark();
        bool contrast = SystemParameters.HighContrast;
        var resources = application.Resources;
        if (IsDark)
        {
            Set(resources, "BgCanvasBrush", MediaColor.FromRgb(0, 0, 0));
            Set(resources, "BgSubtleBrush", MediaColor.FromRgb(0, 0, 0));
            Set(resources, "GlassLightBrush", MediaColor.FromArgb(0xB8, 0x1E, 0x1E, 0x20));
            Set(resources, "GlassStrongBrush", MediaColor.FromArgb(0xDB, 0x2C, 0x2C, 0x2E));
            Set(resources, "GlassBorderBrush", MediaColor.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            Set(resources, "AccentBrush", MediaColor.FromRgb(0x37, 0xA2, 0xEA));
            Set(resources, "AccentHoverBrush", MediaColor.FromRgb(0x5E, 0xC4, 0xF6));
            Set(resources, "AccentActiveBrush", MediaColor.FromRgb(0x1F, 0x86, 0xD6));
            Set(resources, "AccentSoftBrush", MediaColor.FromArgb(0x33, 0x37, 0xA2, 0xEA));
            Set(resources, "TextPrimaryBrush", MediaColor.FromRgb(0xF5, 0xF5, 0xF7));
            Set(resources, "TextSecondaryBrush", contrast ? MediaColor.FromRgb(0xF5, 0xF5, 0xF7) : MediaColor.FromRgb(0xA1, 0xA1, 0xA6));
            Set(resources, "TextTertiaryBrush", MediaColor.FromRgb(0x8E, 0x8E, 0x93));
            Set(resources, "InputBackgroundBrush", MediaColor.FromRgb(0x1C, 0x1C, 0x1E));
            Set(resources, "BorderDefaultBrush", contrast ? MediaColor.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : MediaColor.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            Set(resources, "BgCanvasBrush", MediaColor.FromRgb(0xFF, 0xFF, 0xFF));
            Set(resources, "BgSubtleBrush", MediaColor.FromRgb(0xF5, 0xF7, 0xFA));
            Set(resources, "GlassLightBrush", MediaColor.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
            Set(resources, "GlassStrongBrush", MediaColor.FromArgb(0xDB, 0xFF, 0xFF, 0xFF));
            Set(resources, "GlassBorderBrush", MediaColor.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
            Set(resources, "AccentBrush", MediaColor.FromRgb(0x37, 0xA2, 0xEA));
            Set(resources, "AccentHoverBrush", MediaColor.FromRgb(0x4E, 0xB0, 0xF0));
            Set(resources, "AccentActiveBrush", MediaColor.FromRgb(0x1F, 0x86, 0xD6));
            Set(resources, "AccentSoftBrush", MediaColor.FromRgb(0xE6, 0xF4, 0xFD));
            Set(resources, "TextPrimaryBrush", MediaColor.FromRgb(0x1D, 0x1D, 0x1F));
            Set(resources, "TextSecondaryBrush", contrast ? MediaColor.FromRgb(0x1D, 0x1D, 0x1F) : MediaColor.FromRgb(0x6E, 0x6E, 0x73));
            Set(resources, "TextTertiaryBrush", MediaColor.FromRgb(0x86, 0x86, 0x8B));
            Set(resources, "InputBackgroundBrush", MediaColor.FromRgb(0xFF, 0xFF, 0xFF));
            Set(resources, "BorderDefaultBrush", contrast ? MediaColor.FromArgb(0x33, 0, 0, 0) : MediaColor.FromArgb(0x1A, 0, 0, 0));
        }

        Set(resources, "TextOnAccentBrush", MediaColor.FromRgb(0xFF, 0xFF, 0xFF));
        Set(resources, "BorderSubtleBrush", IsDark ? MediaColor.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : MediaColor.FromArgb(0x0F, 0, 0, 0));
    }

    private static bool ReadDark()
    {
        object? value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1);
        return value is int mode && mode == 0;
    }

    private static void Set(ResourceDictionary resources, string key, MediaColor color)
    {
        resources[key] = new System.Windows.Media.SolidColorBrush(color);
    }
}
