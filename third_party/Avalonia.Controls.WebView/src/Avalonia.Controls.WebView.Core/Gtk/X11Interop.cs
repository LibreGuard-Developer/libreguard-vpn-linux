using System;
using System.Runtime.InteropServices;

namespace Avalonia.Controls.Gtk;

internal static partial class X11Interop
{
    private const string libX11 = "libX11.so.6";

    [DllImport(libX11)]
    public static extern IntPtr XOpenDisplay(IntPtr display);

    [LibraryImport(libX11)]
    public static partial int XMapWindow(IntPtr display, IntPtr window);
    [LibraryImport(libX11)]
    public static partial int XRaiseWindow(IntPtr display, IntPtr window);
    [LibraryImport(libX11)]
    public static partial int XReparentWindow(IntPtr display, IntPtr window, IntPtr parent, int x, int y);
    [LibraryImport(libX11)]
    public static partial int XFlush(IntPtr display);
    [LibraryImport(libX11)]
    public static partial int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
