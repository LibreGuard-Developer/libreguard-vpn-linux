using System;
using Avalonia.Controls;

// ReSharper disable InconsistentNaming
// ReSharper disable once CheckNamespace
namespace Avalonia.Platform;

public sealed class WindowsWebView1EnvironmentRequestedEventArgs : WebViewEnvironmentRequestedEventArgs
{
    internal WindowsWebView1EnvironmentRequestedEventArgs(DeferralManager deferralManager) : base(deferralManager)
    {
    }

    /// <summary>
    /// Gets or sets the enterprise ID for applications that are Windows Information Protection-enabled.
    /// </summary>
    public IntPtr EnterpriseId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the privateNetworkClientServer capability is enabled.
    /// </summary>
    public bool? PrivateNetworkClientServerEnabled { get; set; }
}
