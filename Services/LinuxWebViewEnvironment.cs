using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Gtk;
using Avalonia.Platform;

namespace Libreguard.Vpn.Linux.Services;

internal enum CheckoutWebViewProfile
{
    GtkNativeAccelerated,
    WpeSharedMemory,
    GtkOffscreenCompatibility,
    Browser
}

internal sealed record LinuxGraphicsCapabilities(
    string SessionType,
    bool HasDisplay,
    bool EglInitialized,
    bool HasAccessibleRenderNode,
    string Renderer,
    bool HardwareAccelerated,
    bool ProbeTimedOut = false,
    string? FailureReason = null);

internal sealed record WebViewBackendSelection(
    IReadOnlyList<CheckoutWebViewProfile> Profiles,
    string Reason,
    string RendererFamily,
    bool WpeAvailable);

internal static class LinuxWebViewEnvironment
{
    internal const string ModeEnvironmentVariable = "LIBREGUARD_WEBVIEW_MODE";
    internal const string GtkBackendEnvironmentVariable = "GDK_BACKEND";
    private const string DisableDmaBufRendererEnvironmentVariable = "WEBKIT_DISABLE_DMABUF_RENDERER";
    private static readonly string[] WpeLibraries =
    [
        "libWPEWebKit-2.0.so.1",
        "libWPEBackend-fdo-1.0.so.1",
        "libwpe-1.0.so.1"
    ];

    private static readonly WebViewBackendSelection DefaultSelection = new(
        [CheckoutWebViewProfile.GtkOffscreenCompatibility, CheckoutWebViewProfile.Browser],
        "not-initialized",
        "unknown",
        false);

    // WebView detection loads native libraries and may launch the graphics probe. Keep it
    // out of normal application startup; Selection is first read when checkout creates its
    // NativeWebView and the cached choice is then reused for that checkout session.
    private static readonly Lazy<WebViewBackendSelection> SelectionCache = CreateSelectionCache(CreateSelection);

    internal static WebViewBackendSelection Selection => SelectionCache.Value;

    internal static Lazy<WebViewBackendSelection> CreateSelectionCache(Func<WebViewBackendSelection> factory)
        => new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The desktop application is hosted by Avalonia's X11 backend. On a Wayland
    /// desktop session, GTK can otherwise select Wayland for its private WebView
    /// display even when the embedded host is using XWayland. Keep existing X11
    /// sessions (including Linux Mint) entirely unchanged.
    /// </summary>
    internal static void ConfigureProcessEnvironment()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var currentBackend = Environment.GetEnvironmentVariable(GtkBackendEnvironmentVariable);
        if (!ShouldForceX11GtkBackend(sessionType, display, currentBackend))
        {
            return;
        }

        Environment.SetEnvironmentVariable(GtkBackendEnvironmentVariable, "x11");
        StartupDiagnostics.Log(
            $"webview-gtk-backend-forced backend=x11 reason=wayland-avalonia-x11-host display_present=true prior={Sanitize(currentBackend ?? "unset")}");
    }

    internal static bool ShouldForceX11GtkBackend(string? sessionType, string? display, string? currentBackend)
        => string.Equals(sessionType?.Trim(), "wayland", StringComparison.OrdinalIgnoreCase) &&
           !string.IsNullOrWhiteSpace(display) &&
           !string.Equals(currentBackend?.Trim(), "x11", StringComparison.OrdinalIgnoreCase);

    private static WebViewBackendSelection CreateSelection()
    {
        if (!OperatingSystem.IsLinux())
        {
            return DefaultSelection;
        }

        var capabilities = RunBoundedGraphicsProbe(TimeSpan.FromSeconds(2));
        var wpeAvailable = AreWpeLibrariesAvailable();
        var selection = SelectProfiles(
            capabilities,
            wpeAvailable,
            Environment.GetEnvironmentVariable(ModeEnvironmentVariable));

        StartupDiagnostics.Log(
            $"webview-profile-selected profile={ProfileName(selection.Profiles[0])} " +
            $"reason={Sanitize(selection.Reason)} renderer_family={selection.RendererFamily} " +
            $"session={Sanitize(capabilities.SessionType)} wpe_available={wpeAvailable.ToString().ToLowerInvariant()}");
        StartupDiagnostics.Log(
            $"webview-build identity={PatchedWebViewBuild.Identity} upstream_commit={PatchedWebViewBuild.UpstreamCommit[..12]}");

        return selection;
    }

    public static void Configure(WebViewEnvironmentRequestedEventArgs e, CheckoutWebViewProfile profile)
    {
        if (e is LinuxWpeWebViewEnvironmentRequestedEventArgs linux)
        {
            if (profile == CheckoutWebViewProfile.WpeSharedMemory)
            {
                FileSecurity.EnsurePrivateDirectory(XdgPaths.WebViewDataDirectory);
                FileSecurity.EnsurePrivateDirectory(XdgPaths.WebViewCacheDirectory);
                linux.DataDirectory = XdgPaths.WebViewDataDirectory;
                linux.CacheDirectory = XdgPaths.WebViewCacheDirectory;
                linux.RenderingMode = WpeRenderingMode.Shm;
                StartupDiagnostics.Log("webview-runtime-configured backend=wpe rendering=shm");
            }
            else
            {
                linux.PreferWebKitGtkInstead = true;
            }

            return;
        }

        if (e is not GtkWebViewEnvironmentRequestedEventArgs gtk)
        {
            return;
        }

        // Do this again at the WebView boundary so hosts that do not call
        // ConfigureProcessEnvironment during startup still initialize GTK against
        // the same X11 display as the Avalonia parent.
        ConfigureProcessEnvironment();

        if (profile == CheckoutWebViewProfile.GtkOffscreenCompatibility)
        {
            gtk.ExperimentalOffscreen = true;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DisableDmaBufRendererEnvironmentVariable)))
            {
                Environment.SetEnvironmentVariable(DisableDmaBufRendererEnvironmentVariable, "1");
            }

            StartupDiagnostics.Log("webview-runtime-configured backend=gtk composition=offscreen dmabuf=disabled");
            return;
        }

        gtk.ExperimentalOffscreen = false;
        StartupDiagnostics.Log("webview-runtime-configured backend=gtk composition=native acceleration=enabled");
    }

    public static void Configure(WebViewEnvironmentRequestedEventArgs e)
        => Configure(e, Selection.Profiles.FirstOrDefault(CheckoutWebViewProfile.GtkOffscreenCompatibility));

    internal static WebViewBackendSelection SelectProfiles(
        LinuxGraphicsCapabilities capabilities,
        bool wpeAvailable,
        string? requestedMode)
    {
        var mode = requestedMode?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(mode) && mode != "auto")
        {
            var forced = mode switch
            {
                "gtk-native" => CheckoutWebViewProfile.GtkNativeAccelerated,
                "wpe" => CheckoutWebViewProfile.WpeSharedMemory,
                "gtk-offscreen" => CheckoutWebViewProfile.GtkOffscreenCompatibility,
                "browser" => CheckoutWebViewProfile.Browser,
                _ => (CheckoutWebViewProfile?)null
            };

            if (forced is { } profile)
            {
                return new WebViewBackendSelection(
                    profile == CheckoutWebViewProfile.Browser ? [profile] : [profile, CheckoutWebViewProfile.Browser],
                    $"override-{mode}",
                    RendererFamily(capabilities.Renderer),
                    wpeAvailable);
            }
        }

        var profiles = new List<CheckoutWebViewProfile>();
        var nativeSuitable = IsNativeGtkSuitable(capabilities);
        if (nativeSuitable)
        {
            profiles.Add(CheckoutWebViewProfile.GtkNativeAccelerated);
        }

        if (wpeAvailable)
        {
            profiles.Add(CheckoutWebViewProfile.WpeSharedMemory);
        }

        profiles.Add(CheckoutWebViewProfile.GtkOffscreenCompatibility);
        profiles.Add(CheckoutWebViewProfile.Browser);

        var reason = nativeSuitable
            ? "x11-egl-render-node-hardware"
            : BuildCompatibilityReason(capabilities, wpeAvailable);
        return new WebViewBackendSelection(profiles, reason, RendererFamily(capabilities.Renderer), wpeAvailable);
    }

    internal static bool IsNativeGtkSuitable(LinuxGraphicsCapabilities capabilities)
        => string.Equals(capabilities.SessionType, "x11", StringComparison.OrdinalIgnoreCase) &&
           capabilities.HasDisplay &&
           capabilities.EglInitialized &&
           capabilities.HasAccessibleRenderNode &&
           capabilities.HardwareAccelerated &&
           !IsSoftwareRenderer(capabilities.Renderer);

    internal static string RendererFamily(string? renderer)
    {
        var value = renderer?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Contains("llvmpipe") || value.Contains("softpipe") || value.Contains("software rasterizer")) return "software";
        if (value.Contains("virtualbox") || value.Contains("vbox")) return "virtualbox";
        if (value.Contains("vmware") || value.Contains("svga")) return "vmware";
        if (value.Contains("nvidia")) return "nvidia";
        if (value.Contains("amd") || value.Contains("radeon")) return "amd";
        if (value.Contains("intel")) return "intel";
        return "unknown";
    }

    internal static bool IsSoftwareRenderer(string? renderer)
    {
        var value = renderer?.ToLowerInvariant() ?? string.Empty;
        return value.Contains("llvmpipe") ||
               value.Contains("softpipe") ||
               value.Contains("software rasterizer") ||
               value.Contains("swrast") ||
               ((value.Contains("virtualbox") || value.Contains("vbox") || value.Contains("vmware")) &&
                !value.Contains("3d") && !value.Contains("accelerated"));
    }

    internal static string ProfileName(CheckoutWebViewProfile profile) => profile switch
    {
        CheckoutWebViewProfile.GtkNativeAccelerated => "gtk-native",
        CheckoutWebViewProfile.WpeSharedMemory => "wpe-shm",
        CheckoutWebViewProfile.GtkOffscreenCompatibility => "gtk-offscreen",
        _ => "browser"
    };

    private static string BuildCompatibilityReason(LinuxGraphicsCapabilities capabilities, bool wpeAvailable)
    {
        if (capabilities.ProbeTimedOut) return wpeAvailable ? "graphics-probe-timeout-wpe" : "graphics-probe-timeout-compatibility";
        if (!string.IsNullOrWhiteSpace(capabilities.FailureReason))
        {
            var suffix = wpeAvailable ? "wpe" : "compatibility";
            return $"graphics-probe-{Sanitize(capabilities.FailureReason)}-{suffix}";
        }
        if (!string.Equals(capabilities.SessionType, "x11", StringComparison.OrdinalIgnoreCase)) return wpeAvailable ? "non-x11-wpe" : "non-x11-compatibility";
        if (!capabilities.HasDisplay) return wpeAvailable ? "display-unavailable-wpe" : "display-unavailable-compatibility";
        if (!capabilities.EglInitialized) return wpeAvailable ? "egl-unavailable-wpe" : "egl-unavailable-compatibility";
        if (!capabilities.HasAccessibleRenderNode) return wpeAvailable ? "render-node-unavailable-wpe" : "render-node-unavailable-compatibility";
        return wpeAvailable ? "software-renderer-wpe" : "software-renderer-compatibility";
    }

    private static LinuxGraphicsCapabilities RunBoundedGraphicsProbe(TimeSpan timeout)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return FailedProbe("process-path-unavailable");
            }

            var startInfo = new ProcessStartInfo(processPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entryAssembly = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(entryAssembly))
                {
                    return FailedProbe("entry-assembly-unavailable");
                }

                startInfo.ArgumentList.Add(entryAssembly);
            }

            startInfo.ArgumentList.Add(LinuxGraphicsProbe.CommandLineArgument);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return FailedProbe("probe-start-failed");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return FailedProbe("timeout") with { ProbeTimedOut = true };
            }

            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return FailedProbe("probe-failed");
            }

            return JsonSerializer.Deserialize<LinuxGraphicsCapabilities>(output) ?? FailedProbe("invalid-output");
        }
        catch (Exception ex)
        {
            return FailedProbe(ex.GetType().Name);
        }
    }

    private static LinuxGraphicsCapabilities FailedProbe(string reason)
        => new(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown",
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")),
            false,
            false,
            "unknown",
            false,
            false,
            reason);

    private static bool AreWpeLibrariesAvailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        foreach (var library in WpeLibraries)
        {
            if (!NativeLibrary.TryLoad(library, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
        }

        return true;
    }

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '-').Replace('"', '\'');
}
