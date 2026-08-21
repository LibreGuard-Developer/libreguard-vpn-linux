using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia.Controls.Win.WebView1.Interop;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Avalonia.Controls.Win.WebView1;

[SupportedOSPlatform("windows")]
internal class WebView1Process
{
    private static readonly Dictionary<ProcessOptions, WebView1Process> s_processes = new();
    private readonly IWebViewControlProcess _process;
    private readonly ProcessOptions _options;
    private int _controlsCount;

    static WebView1Process()
    {
        Dispatcher.UIThread.ShutdownFinished += (sender, args) =>
        {
            foreach (var process in s_processes.Values.ToArray())
            {
                s_processes.Remove(process._options);
                process._process.Terminate();
            }
        };
    }

    private WebView1Process(ProcessOptions options, IWebViewControlProcess process)
    {
        _options = options;
        _process = process;
    }
    
    public static WebView1Process GetOrCreateProcess(WindowsWebView1EnvironmentRequestedEventArgs args)
    {
        WebViewDispatcher.VerifyAccess();

        var mOptions = new ProcessOptions(args.EnterpriseId, args.PrivateNetworkClientServerEnabled);
        if (!s_processes.TryGetValue(mOptions, out var process))
        {
            var options = NativeWinRTMethods.CreateInstance<IWebViewControlProcessOptions>("Windows.Web.UI.Interop.WebViewControlProcessOptions")
                          ?? throw new InvalidOperationException("Unable to create WebViewControlProcessOptions.");
            options.put_PrivateNetworkClientServerCapability(mOptions.PrivateNetworkClientServerEnabled switch
            {
                true => WebViewControlProcessCapabilityState.Enabled,
                false => WebViewControlProcessCapabilityState.Disabled,
                _ => WebViewControlProcessCapabilityState.Default
            });
            options.put_EnterpriseId(mOptions.EnterpriseId);

            var factory = NativeWinRTMethods.CreateActivationFactory<IWebViewControlProcessFactory>("Windows.Web.UI.Interop.WebViewControlProcess")
                          ?? throw new InvalidOperationException("Unable to create WebViewControlProcess.");
            s_processes[mOptions] = process = new WebView1Process(mOptions, factory.CreateWithOptions(options));
        }

        return process;
    }

    public Task<IWebViewControl> CreateWebViewControl(IntPtr handle, int width, int height)
    {
        var operation = _process.CreateWebViewControl((long)handle,
            new winrtRect { Height = width, Width = height });
        var handler = new WebViewControlHandler();
        operation.put_Completed(handler);
        return handler.Task;
    }

    public void AddOne()
    {
        WebViewDispatcher.VerifyAccess();
        _controlsCount += 1;
    }

    public void ReleaseOne()
    {
        WebViewDispatcher.VerifyAccess();
        _controlsCount -= 1;
        if (_controlsCount == 0)
        {
            s_processes.Remove(_options);
            _process.Terminate();
        }
    }

    private record ProcessOptions(IntPtr EnterpriseId, bool? PrivateNetworkClientServerEnabled);
}
