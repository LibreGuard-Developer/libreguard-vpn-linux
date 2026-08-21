#if !ANDROID && NET8_0_OR_GREATER 
using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Avalonia.Controls.Browser;

[SupportedOSPlatform("browser")]
internal static class BrowserWebAuthenticationBroker
{
    public static async Task<Uri> AuthenticateAsync(TopLevel _, Uri requestUri, Uri redirectUri)
    {
        await WebViewInterop.EnsureLoaded();

        var windowId = Guid.NewGuid().ToString("N");

        try
        {
            var uri = await WebViewInterop.OpenAuthWindow(windowId, requestUri.ToString(), redirectUri.ToString());
            return uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var resultUri) ?
                throw new OperationCanceledException() :
                resultUri;
        }
        catch (JSException jsException)
        {
            throw new OperationCanceledException(jsException.Message);
        }
        finally
        {
            try
            {
                await WebViewInterop.CloseAuthWindow(windowId);
            }
            catch
            {
                // ignore
            }
        }
    }
}
#endif
