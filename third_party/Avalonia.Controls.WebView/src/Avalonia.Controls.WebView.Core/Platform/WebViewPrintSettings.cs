// ReSharper disable once CheckNamespace

namespace Avalonia.Platform;

/// <summary>
/// Page orientation for printing.
/// </summary>
public enum WebViewPrintOrientation
{
    /// <summary>Portrait orientation</summary>
    Portrait,

    /// <summary>Landscape orientation</summary>
    Landscape
}

/// <summary>
/// Print settings for WebView.
/// </summary>
public record WebViewPrintSettings
{
    /// <summary>Page orientation</summary>
    public WebViewPrintOrientation Orientation { get; init; }

    /// <summary>Top margin in pixels</summary>
    public int MarginTop { get; init; } = 0;

    /// <summary>Bottom margin in pixels</summary>
    public int MarginBottom { get; init; } = 0;

    /// <summary>Left margin in pixels</summary>
    public int MarginLeft { get; init; } = 0;

    /// <summary>Right margin in pixels</summary>
    public int MarginRight { get; init; } = 0;

    /// <summary>Scaling factor</summary>
    public double ScaleFactor { get; init; } = 1.0;
}
