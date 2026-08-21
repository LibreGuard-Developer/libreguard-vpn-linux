using System;
using System.Runtime.InteropServices;

namespace Avalonia.Controls.Macios.Interop.AuthenticationServices;

internal partial class AuthenticationServices
{
    private const string WebKitFramework = "/System/Library/Frameworks/AuthenticationServices.framework/AuthenticationServices";

    [LibraryImport(WebKitFramework, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getClass(string className);
    [LibraryImport(WebKitFramework, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr objc_getProtocol(string name);
}

