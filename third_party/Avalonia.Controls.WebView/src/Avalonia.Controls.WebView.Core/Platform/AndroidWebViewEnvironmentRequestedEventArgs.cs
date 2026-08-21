using Avalonia.Controls;

// ReSharper disable InconsistentNaming
// ReSharper disable once CheckNamespace
namespace Avalonia.Platform;

public sealed class AndroidWebViewEnvironmentRequestedEventArgs : WebViewEnvironmentRequestedEventArgs
{
    internal AndroidWebViewEnvironmentRequestedEventArgs(DeferralManager deferralManager) : base(deferralManager)
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether built-in zoom controls are enabled in the Android WebView.
    /// </summary>
    public bool BuiltInZoomControls { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether DOM storage is enabled in the Android WebView.
    /// </summary>
    public bool DomStorageEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the database storage API is enabled in the Android WebView.
    /// </summary>
    public bool DatabaseEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the application name to append to the default user agent string.
    /// </summary>
    public string? ApplicationNameForUserAgent { get; set; }

    /// <summary>
    /// Gets or sets the base directory for website data. Can only be set once per Android process.
    /// </summary>
    public string? DataDirectorySuffix{ get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to disable the cache completely.
    /// </summary>
    public bool DisableCache { get; set; } = false;
}
