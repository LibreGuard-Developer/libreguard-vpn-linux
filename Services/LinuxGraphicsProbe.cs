using System.Runtime.InteropServices;
using System.Text.Json;

namespace Libreguard.Vpn.Linux.Services;

internal static class LinuxGraphicsProbe
{
    internal const string CommandLineArgument = "--graphics-probe";
    private const int EglNone = 0x3038;
    private const int EglSurfaceType = 0x3033;
    private const int EglPbufferBit = 0x0001;
    private const int EglRenderableType = 0x3040;
    private const int EglOpenGlBit = 0x0008;
    private const int EglRedSize = 0x3024;
    private const int EglGreenSize = 0x3023;
    private const int EglBlueSize = 0x3022;
    private const int EglWidth = 0x3057;
    private const int EglHeight = 0x3056;
    private const uint EglOpenGlApi = 0x30A2;
    private const uint GlRenderer = 0x1F01;

    internal static bool TryHandle(string[] args, out int exitCode)
    {
        if (!args.Contains(CommandLineArgument, StringComparer.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        try
        {
            Console.Out.Write(JsonSerializer.Serialize(Probe()));
            exitCode = 0;
        }
        catch
        {
            exitCode = 1;
        }

        return true;
    }

    internal static LinuxGraphicsCapabilities Probe()
    {
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(session))
        {
            session = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ? "wayland" :
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ? "x11" : "unknown";
        }

        var hasDisplay = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        var hasRenderNode = HasAccessibleRenderNode();
        var (eglInitialized, renderer) = ProbeEglRenderer();
        var hardware = eglInitialized && hasRenderNode &&
                       !string.IsNullOrWhiteSpace(renderer) &&
                       !LinuxWebViewEnvironment.IsSoftwareRenderer(renderer) &&
                       !string.Equals(renderer, "unknown", StringComparison.OrdinalIgnoreCase);
        return new LinuxGraphicsCapabilities(session, hasDisplay, eglInitialized, hasRenderNode, renderer, hardware);
    }

    private static bool HasAccessibleRenderNode()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles("/dev/dri", "renderD*"))
            {
                try
                {
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    return true;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static (bool Initialized, string Renderer) ProbeEglRenderer()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libEGL.so.1", out var egl))
        {
            return (false, "unknown");
        }

        IntPtr display = IntPtr.Zero;
        IntPtr surface = IntPtr.Zero;
        IntPtr context = IntPtr.Zero;
        try
        {
            var getDisplay = Load<EglGetDisplay>(egl, "eglGetDisplay");
            var initialize = Load<EglInitialize>(egl, "eglInitialize");
            var bindApi = Load<EglBindApi>(egl, "eglBindAPI");
            var chooseConfig = Load<EglChooseConfig>(egl, "eglChooseConfig");
            var createSurface = Load<EglCreatePbufferSurface>(egl, "eglCreatePbufferSurface");
            var createContext = Load<EglCreateContext>(egl, "eglCreateContext");
            var makeCurrent = Load<EglMakeCurrent>(egl, "eglMakeCurrent");
            var destroySurface = Load<EglDestroySurface>(egl, "eglDestroySurface");
            var destroyContext = Load<EglDestroyContext>(egl, "eglDestroyContext");
            var terminate = Load<EglTerminate>(egl, "eglTerminate");

            display = getDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero || initialize(display, out _, out _) == 0)
            {
                return (false, "unknown");
            }

            if (bindApi(EglOpenGlApi) == 0)
            {
                return (true, "unknown");
            }

            int[] configAttributes =
            [
                EglSurfaceType, EglPbufferBit,
                EglRenderableType, EglOpenGlBit,
                EglRedSize, 8,
                EglGreenSize, 8,
                EglBlueSize, 8,
                EglNone
            ];
            var configs = new IntPtr[1];
            if (chooseConfig(display, configAttributes, configs, configs.Length, out var count) == 0 || count == 0)
            {
                return (true, "unknown");
            }

            int[] surfaceAttributes = [EglWidth, 1, EglHeight, 1, EglNone];
            int[] contextAttributes = [EglNone];
            surface = createSurface(display, configs[0], surfaceAttributes);
            context = createContext(display, configs[0], IntPtr.Zero, contextAttributes);
            if (surface == IntPtr.Zero || context == IntPtr.Zero || makeCurrent(display, surface, surface, context) == 0)
            {
                return (true, "unknown");
            }

            if (!NativeLibrary.TryLoad("libGL.so.1", out var gl))
            {
                return (true, "unknown");
            }

            try
            {
                var getString = Load<GlGetString>(gl, "glGetString");
                var rendererPointer = getString(GlRenderer);
                return (true, Marshal.PtrToStringAnsi(rendererPointer) ?? "unknown");
            }
            finally
            {
                NativeLibrary.Free(gl);
            }
        }
        catch
        {
            return (display != IntPtr.Zero, "unknown");
        }
        finally
        {
            try
            {
                if (display != IntPtr.Zero)
                {
                    var makeCurrent = Load<EglMakeCurrent>(egl, "eglMakeCurrent");
                    makeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    if (surface != IntPtr.Zero) Load<EglDestroySurface>(egl, "eglDestroySurface")(display, surface);
                    if (context != IntPtr.Zero) Load<EglDestroyContext>(egl, "eglDestroyContext")(display, context);
                    Load<EglTerminate>(egl, "eglTerminate")(display);
                }
            }
            catch
            {
            }

            NativeLibrary.Free(egl);
        }
    }

    private static T Load<T>(IntPtr library, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EglGetDisplay(IntPtr displayId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglInitialize(IntPtr display, out int major, out int minor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglBindApi(uint api);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglChooseConfig(IntPtr display, int[] attributes, IntPtr[] configs, int configSize, out int configCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attributes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int[] attributes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglDestroySurface(IntPtr display, IntPtr surface);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglDestroyContext(IntPtr display, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EglTerminate(IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GlGetString(uint name);
}
