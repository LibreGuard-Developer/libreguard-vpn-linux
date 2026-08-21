using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Avalonia.Controls.Win.WebView1;

[SupportedOSPlatform("windows")]
internal class HStringInterop(string? s) : IDisposable
{
    private IntPtr _s = s == null ? IntPtr.Zero : WindowsCreateString(s);

    public IntPtr Handle => _s;

    public void Dispose()
    {
        if (_s != IntPtr.Zero)
        {
            NativeWinRTMethods.WindowsDeleteString(_s);
            _s = IntPtr.Zero;
        }
    }

    public static unsafe string? FromIntPtr(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return null;
        uint length;
        var buffer = NativeWinRTMethods.WindowsGetStringRawBuffer(pointer, &length);
        return new string(buffer, 0, (int)length);
    }

    private static unsafe IntPtr WindowsCreateString(string? value)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }
        IntPtr handle;
        fixed (char* lpValue = value)
        {
            Marshal.ThrowExceptionForHR(
                NativeWinRTMethods.WindowsCreateString((ushort*)lpValue, value.Length, &handle));
        }
        return handle;
    }
}
