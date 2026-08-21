using Avalonia.Styling;

namespace Libreguard.Vpn.Linux.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

internal static class AppThemePreferenceExtensions
{
    public static ThemeVariant ToRequestedThemeVariant(this AppThemePreference preference)
        => preference switch
        {
            AppThemePreference.Light => ThemeVariant.Light,
            AppThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

    public static AppThemePreference Parse(string? value)
    {
        if (Enum.TryParse<AppThemePreference>(value, ignoreCase: true, out var preference))
        {
            return preference;
        }

        return AppThemePreference.System;
    }
}
