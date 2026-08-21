using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Views;

internal sealed class WebViewSmokeWindow : Window
{
    private const string ExpectedInput = "a19Z";
    private const string SmokeHtml = """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <style>
            html, body { margin: 0; width: 100%; height: 100%; background: white; overflow: hidden; }
            #checkout-input { position: absolute; left: 40px; top: 48px; width: 300px; height: 48px; font: 24px sans-serif; }
            #after-input { position: absolute; left: 380px; top: 48px; width: 120px; height: 48px; }
            #blue-swatch { position: absolute; left: 40px; top: 180px; width: 96px; height: 96px; background: rgb(0, 102, 255); }
          </style>
        </head>
        <body>
          <input id="checkout-input" aria-label="Checkout input" autocomplete="off">
          <button id="after-input">After input</button>
          <div id="blue-swatch"></div>
          <script>document.documentElement.dataset.smokeReady = 'true';</script>
        </body>
        </html>
        """;

    private readonly NativeWebView _webView;
    private int _completed;
    private int _validationStarted;
    private bool _navigationRequested;

    public WebViewSmokeWindow()
    {
        Title = "LibreGuard WebView Smoke";
        Width = 640;
        Height = 480;
        ShowInTaskbar = false;
        _webView = new NativeWebView();
        _webView.EnvironmentRequested += HandleEnvironmentRequested;
        _webView.AdapterCreated += HandleAdapterCreated;
        _webView.NavigationCompleted += HandleNavigationCompleted;
        Content = _webView;
        Loaded += HandleLoaded;
    }

    public event Action<int>? Completed;

    private async void HandleLoaded(object? sender, RoutedEventArgs e)
    {
        if (LinuxWebViewEnvironment.Selection.Profiles[0] == CheckoutWebViewProfile.Browser)
        {
            Complete(0, "webview-smoke-browser-profile-selected");
            return;
        }

        _navigationRequested = true;
        _webView.NavigateToString(SmokeHtml);
        await Task.Delay(TimeSpan.FromSeconds(25));
        Complete(2, "webview-smoke-timeout");
    }

    private void HandleEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        LinuxWebViewEnvironment.Configure(e);
    }

    private void HandleAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        var adapter = _webView.AdapterInfo?.ToString() ?? e.TryGetPlatformHandle()?.GetType().Name ?? "unknown";
        Console.WriteLine($"WebView adapter: {adapter}");
        StartupDiagnostics.Log($"webview-smoke-adapter adapter={Sanitize(adapter)}");
    }

    private async void HandleNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!_navigationRequested || Interlocked.Exchange(ref _validationStarted, 1) != 0)
        {
            return;
        }

        if (!e.IsSuccess)
        {
            Complete(3, "webview-smoke-navigation success=false");
            return;
        }

        try
        {
            await WaitForDocumentAsync();
            Activate();
            _webView.Focus();

            var inputPoint = _webView.PointToScreen(new Point(100, 72));
            using var x11 = X11SmokeInput.Open();
            x11.Click(inputPoint.X, inputPoint.Y);
            x11.Type("a1x");
            x11.Press("BackSpace");
            x11.Type("9");
            x11.TypeShifted("z");
            x11.Press("Tab");
            x11.Flush();

            await Task.Delay(500);
            var result = await _webView.InvokeScript(
                "document.getElementById('checkout-input').value + '|' + document.activeElement.id");
            if (!string.Equals(result, ExpectedInput + "|after-input", StringComparison.Ordinal))
            {
                Complete(4, $"webview-smoke-input-failed actual={Sanitize(result ?? "null")}");
                return;
            }

            var swatchPoint = _webView.PointToScreen(new Point(80, 220));
            var color = x11.ReadScreenPixel(swatchPoint.X, swatchPoint.Y);
            if (color.Blue < 220 || color.Red > 35 || color.Green is < 70 or > 140)
            {
                Complete(5, $"webview-smoke-color-failed rgb={color.Red},{color.Green},{color.Blue}");
                return;
            }

            Complete(0,
                $"webview-smoke-success input={ExpectedInput} focus=after-input rgb={color.Red},{color.Green},{color.Blue}");
        }
        catch (Exception ex)
        {
            Complete(6, $"webview-smoke-validation-error type={ex.GetType().Name} message={Sanitize(ex.Message)}");
        }
    }

    private async Task WaitForDocumentAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (string.Equals(
                    await _webView.InvokeScript("document.documentElement.dataset.smokeReady"),
                    "true",
                    StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Deterministic smoke document did not become ready.");
    }

    private void Complete(int exitCode, string message)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        Console.WriteLine(message);
        StartupDiagnostics.Log(message);
        Completed?.Invoke(exitCode);
    }

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '-').Replace('"', '\'');

    private sealed class X11SmokeInput : IDisposable
    {
        private const string LibX11 = "libX11.so.6";
        private const string LibXtst = "libXtst.so.6";
        private const int ZPixmap = 2;
        private readonly IntPtr _display;
        private readonly int _screen;

        private X11SmokeInput(IntPtr display)
        {
            _display = display;
            _screen = XDefaultScreen(display);
        }

        public static X11SmokeInput Open()
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("The WebView smoke input test requires Linux/X11.");

            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                throw new InvalidOperationException("XOpenDisplay failed.");
            return new X11SmokeInput(display);
        }

        public void Click(int x, int y)
        {
            EnsureSucceeded(XTestFakeMotionEvent(_display, _screen, x, y, 0), "XTestFakeMotionEvent");
            EnsureSucceeded(XTestFakeButtonEvent(_display, 1, true, 0), "XTestFakeButtonEvent(press)");
            EnsureSucceeded(XTestFakeButtonEvent(_display, 1, false, 0), "XTestFakeButtonEvent(release)");
            Flush();
        }

        public void Type(string text)
        {
            foreach (var character in text)
            {
                Press(character.ToString());
            }
        }

        public void TypeShifted(string key)
        {
            var shift = ResolveKeyCode("Shift_L");
            EnsureSucceeded(XTestFakeKeyEvent(_display, shift, true, 0), "XTestFakeKeyEvent(shift-press)");
            Press(key);
            EnsureSucceeded(XTestFakeKeyEvent(_display, shift, false, 0), "XTestFakeKeyEvent(shift-release)");
        }

        public void Press(string key)
        {
            var keyCode = ResolveKeyCode(key);
            EnsureSucceeded(XTestFakeKeyEvent(_display, keyCode, true, 0), $"XTestFakeKeyEvent({key}-press)");
            EnsureSucceeded(XTestFakeKeyEvent(_display, keyCode, false, 0), $"XTestFakeKeyEvent({key}-release)");
        }

        public void Flush()
        {
            XFlush(_display);
            XSync(_display, false);
        }

        public (byte Red, byte Green, byte Blue) ReadScreenPixel(int x, int y)
        {
            Flush();
            var root = XDefaultRootWindow(_display);
            var image = XGetImage(_display, root, x, y, 1, 1, nuint.MaxValue, ZPixmap);
            if (image == IntPtr.Zero)
                throw new InvalidOperationException("XGetImage failed.");

            try
            {
                var color = new XColor { Pixel = XGetPixel(image, 0, 0) };
                var colormap = XDefaultColormap(_display, _screen);
                EnsureSucceeded(XQueryColor(_display, colormap, ref color), "XQueryColor");
                return ((byte)(color.Red >> 8), (byte)(color.Green >> 8), (byte)(color.Blue >> 8));
            }
            finally
            {
                XDestroyImage(image);
            }
        }

        public void Dispose() => XCloseDisplay(_display);

        private uint ResolveKeyCode(string key)
        {
            var keySym = XStringToKeysym(key);
            var keyCode = XKeysymToKeycode(_display, keySym);
            if (keySym == 0 || keyCode == 0)
                throw new InvalidOperationException($"X11 key could not be resolved: {key}");
            return keyCode;
        }

        private static void EnsureSucceeded(int result, string operation)
        {
            if (result == 0)
                throw new InvalidOperationException($"{operation} failed.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XColor
        {
            public nuint Pixel;
            public ushort Red;
            public ushort Green;
            public ushort Blue;
            public byte Flags;
            public byte Padding;
        }

        [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr displayName);
        [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
        [DllImport(LibX11)] private static extern int XDefaultScreen(IntPtr display);
        [DllImport(LibX11)] private static extern nuint XDefaultRootWindow(IntPtr display);
        [DllImport(LibX11)] private static extern nuint XDefaultColormap(IntPtr display, int screen);
        [DllImport(LibX11)] private static extern nuint XStringToKeysym([MarshalAs(UnmanagedType.LPStr)] string value);
        [DllImport(LibX11)] private static extern uint XKeysymToKeycode(IntPtr display, nuint keySym);
        [DllImport(LibX11)] private static extern int XFlush(IntPtr display);
        [DllImport(LibX11)] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.I4)] bool discard);
        [DllImport(LibX11)] private static extern IntPtr XGetImage(IntPtr display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);
        [DllImport(LibX11)] private static extern nuint XGetPixel(IntPtr image, int x, int y);
        [DllImport(LibX11)] private static extern int XDestroyImage(IntPtr image);
        [DllImport(LibX11)] private static extern int XQueryColor(IntPtr display, nuint colormap, ref XColor color);
        [DllImport(LibXtst)] private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, nuint delay);
        [DllImport(LibXtst)] private static extern int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.I4)] bool isPress, nuint delay);
        [DllImport(LibXtst)] private static extern int XTestFakeKeyEvent(IntPtr display, uint keyCode, [MarshalAs(UnmanagedType.I4)] bool isPress, nuint delay);
    }
}
