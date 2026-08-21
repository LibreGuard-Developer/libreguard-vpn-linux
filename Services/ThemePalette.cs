using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Libreguard.Vpn.Linux.Services;

internal static class ThemePalette
{
    public static void Apply(Application application, ThemeVariant actualThemeVariant)
    {
        var palette = actualThemeVariant == ThemeVariant.Dark ? Dark : Light;
        SetColorAndBrush(application, "PrimaryColor", "PrimaryBrush", palette.PrimaryColor);
        SetColorAndBrush(application, "PrimaryForegroundColor", "PrimaryForegroundBrush", palette.PrimaryForegroundColor);
        SetColorAndBrush(application, "BackgroundColor", "BackgroundBrush", palette.BackgroundColor);
        SetColorAndBrush(application, "ForegroundColor", "ForegroundBrush", palette.ForegroundColor);
        SetColorAndBrush(application, "CardColor", "CardBrush", palette.CardColor);
        SetColorAndBrush(application, "SecondaryColor", "SecondaryBrush", palette.SecondaryColor);
        SetColorAndBrush(application, "MutedColor", "MutedBrush", palette.MutedColor);
        SetColorAndBrush(application, "MutedForegroundColor", "MutedForegroundBrush", palette.MutedForegroundColor);
        SetColorAndBrush(application, "BorderColor", "BorderBrush", palette.BorderColor);
        SetColorAndBrush(application, "DestructiveColor", "DestructiveBrush", palette.DestructiveColor);
        SetColorAndBrush(application, "WarningColor", "WarningBrush", palette.WarningColor);
        SetColorAndBrush(application, "StatusConnectedColor", "StatusConnectedBrush", palette.StatusConnectedColor);
        SetColorAndBrush(application, "StatusConnectingColor", "StatusConnectingBrush", palette.StatusConnectingColor);
        SetColorAndBrush(application, "StatusDisconnectedColor", "StatusDisconnectedBrush", palette.StatusDisconnectedColor);

        application.Resources["PrimaryLightBrush"] = new SolidColorBrush(palette.PrimaryLightColor);
        application.Resources["PrimaryMediumBrush"] = new SolidColorBrush(palette.PrimaryMediumColor);
        application.Resources["DestructiveLightBrush"] = new SolidColorBrush(palette.DestructiveLightColor);
        application.Resources["StatusConnectedLightBrush"] = new SolidColorBrush(palette.StatusConnectedLightColor);
        application.Resources["StatusConnectingLightBrush"] = new SolidColorBrush(palette.StatusConnectingLightColor);
        application.Resources["StatusDisconnectedLightBrush"] = new SolidColorBrush(palette.StatusDisconnectedLightColor);
        application.Resources["BrandPanelBrush"] = CreateGradientBrush(palette.BrandPanelStartColor, palette.BrandPanelEndColor);
        application.Resources["UpgradeBrush"] = CreateGradientBrush(palette.UpgradeStartColor, palette.UpgradeEndColor);
    }

    private static void SetColorAndBrush(Application application, string colorKey, string brushKey, Color color)
    {
        application.Resources[colorKey] = color;
        application.Resources[brushKey] = new SolidColorBrush(color);
    }

    private static LinearGradientBrush CreateGradientBrush(Color start, Color end)
        => new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1)
            }
        };

    private readonly record struct ThemePaletteValues(
        Color PrimaryColor,
        Color PrimaryForegroundColor,
        Color BackgroundColor,
        Color ForegroundColor,
        Color CardColor,
        Color SecondaryColor,
        Color MutedColor,
        Color MutedForegroundColor,
        Color BorderColor,
        Color DestructiveColor,
        Color WarningColor,
        Color StatusConnectedColor,
        Color StatusConnectingColor,
        Color StatusDisconnectedColor,
        Color PrimaryLightColor,
        Color PrimaryMediumColor,
        Color DestructiveLightColor,
        Color StatusConnectedLightColor,
        Color StatusConnectingLightColor,
        Color StatusDisconnectedLightColor,
        Color BrandPanelStartColor,
        Color BrandPanelEndColor,
        Color UpgradeStartColor,
        Color UpgradeEndColor);

    private static readonly ThemePaletteValues Light = new(
        PrimaryColor: Color.FromRgb(21, 112, 239),
        PrimaryForegroundColor: Color.FromRgb(255, 255, 255),
        BackgroundColor: Color.FromRgb(255, 255, 255),
        ForegroundColor: Color.FromRgb(17, 24, 39),
        CardColor: Color.FromRgb(248, 250, 252),
        SecondaryColor: Color.FromRgb(226, 232, 240),
        MutedColor: Color.FromRgb(241, 245, 249),
        MutedForegroundColor: Color.FromRgb(100, 116, 139),
        BorderColor: Color.FromRgb(215, 223, 235),
        DestructiveColor: Color.FromRgb(239, 68, 68),
        WarningColor: Color.FromRgb(245, 158, 11),
        StatusConnectedColor: Color.FromRgb(16, 185, 129),
        StatusConnectingColor: Color.FromRgb(245, 158, 11),
        StatusDisconnectedColor: Color.FromRgb(148, 163, 184),
        PrimaryLightColor: Color.FromArgb(26, 21, 112, 239),
        PrimaryMediumColor: Color.FromArgb(51, 21, 112, 239),
        DestructiveLightColor: Color.FromArgb(26, 239, 68, 68),
        StatusConnectedLightColor: Color.FromArgb(26, 16, 185, 129),
        StatusConnectingLightColor: Color.FromArgb(26, 245, 158, 11),
        StatusDisconnectedLightColor: Color.FromArgb(26, 148, 163, 184),
        BrandPanelStartColor: Color.FromRgb(242, 247, 255),
        BrandPanelEndColor: Color.FromRgb(255, 255, 255),
        UpgradeStartColor: Color.FromArgb(26, 21, 112, 239),
        UpgradeEndColor: Color.FromArgb(0, 255, 255, 255));

    private static readonly ThemePaletteValues Dark = new(
        PrimaryColor: Color.FromRgb(21, 112, 239),
        PrimaryForegroundColor: Color.FromRgb(255, 255, 255),
        BackgroundColor: Color.FromRgb(11, 18, 32),
        ForegroundColor: Color.FromRgb(229, 238, 249),
        CardColor: Color.FromRgb(17, 24, 39),
        SecondaryColor: Color.FromRgb(30, 41, 59),
        MutedColor: Color.FromRgb(23, 32, 51),
        MutedForegroundColor: Color.FromRgb(148, 163, 184),
        BorderColor: Color.FromRgb(36, 50, 68),
        DestructiveColor: Color.FromRgb(248, 113, 113),
        WarningColor: Color.FromRgb(251, 191, 36),
        StatusConnectedColor: Color.FromRgb(52, 211, 153),
        StatusConnectingColor: Color.FromRgb(251, 191, 36),
        StatusDisconnectedColor: Color.FromRgb(148, 163, 184),
        PrimaryLightColor: Color.FromArgb(32, 21, 112, 239),
        PrimaryMediumColor: Color.FromArgb(60, 21, 112, 239),
        DestructiveLightColor: Color.FromArgb(32, 248, 113, 113),
        StatusConnectedLightColor: Color.FromArgb(28, 52, 211, 153),
        StatusConnectingLightColor: Color.FromArgb(28, 251, 191, 36),
        StatusDisconnectedLightColor: Color.FromArgb(28, 148, 163, 184),
        BrandPanelStartColor: Color.FromRgb(18, 35, 58),
        BrandPanelEndColor: Color.FromRgb(11, 18, 32),
        UpgradeStartColor: Color.FromArgb(38, 21, 112, 239),
        UpgradeEndColor: Color.FromArgb(0, 11, 18, 32));
}
