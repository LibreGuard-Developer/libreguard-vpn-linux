using Avalonia;
using Libreguard.Vpn.Linux.Services;
using System.Collections;
using System.Runtime.InteropServices;

namespace Libreguard.Vpn.Linux;

internal static class Program
{
    private const string LinuxDesktopWmClass = "libreguard-vpn-linux";

    [STAThread]
    public static void Main(string[] args)
    {
        LinuxWebViewEnvironment.ConfigureProcessEnvironment();

        if (LinuxGraphicsProbe.TryHandle(args, out var graphicsProbeExitCode))
        {
            Environment.ExitCode = graphicsProbeExitCode;
            return;
        }

        var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance.Status == SingleInstanceAcquireStatus.AlreadyRunning)
        {
            StartupDiagnostics.Log($"single-instance-rejected lock_path=\"{singleInstance.LockPath}\"");
            Console.Error.WriteLine("LibreGuard is already running.");
            Environment.ExitCode = 0;
            return;
        }

        if (singleInstance.Status != SingleInstanceAcquireStatus.Acquired || singleInstance.Guard is null)
        {
            StartupDiagnostics.Log($"single-instance-failed lock_path=\"{singleInstance.LockPath}\" error=\"{singleInstance.ErrorMessage}\"");
            Console.Error.WriteLine($"Unable to start LibreGuard safely: {singleInstance.ErrorMessage}");
            Environment.ExitCode = 1;
            return;
        }

        using var singleInstanceGuard = singleInstance.Guard;
        StartupDiagnostics.Log($"single-instance-acquired lock_path=\"{singleInstance.LockPath}\"");

        try
        {
            var flags = string.Join(",", args
                .Where(argument => argument.StartsWith("--", StringComparison.Ordinal))
                .Select(argument => argument.Split('=', 2)[0]));
            StartupDiagnostics.Log($"process-start pid={Environment.ProcessId} arg_count={args.Length} flags=\"{flags}\"");
            BuildIdentity.Log();
            StartupDiagnostics.Log(BuildStartupDiagnostics());

            StartupDiagnostics.Log("avalonia-app-builder-create");
            var appBuilder = BuildAvaloniaApp();

            StartupDiagnostics.Log("avalonia-classic-lifetime-start");
            appBuilder.StartWithClassicDesktopLifetime(args);
            StartupDiagnostics.Log("avalonia-classic-lifetime-exit");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("startup-exception");
            Console.Error.WriteLine(BuildStartupDiagnostics());
            Console.Error.WriteLine($"Startup failed: {ex.GetType().Name}");
            StartupDiagnostics.Log($"Startup failed type={ex.GetType().Name}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                WmClass = LinuxDesktopWmClass
            })
            .WithInterFont()
            .LogToTrace();
    }

    public static string BuildStartupDiagnostics(IDictionary<string, string?>? environmentVariables = null)
    {
        environmentVariables ??= Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => entry.Key?.ToString() ?? string.Empty, entry => entry.Value?.ToString());

        var lines = new List<string>
        {
            "=== LibreGuard startup diagnostics ===",
            $"Runtime={RuntimeInformation.FrameworkDescription}",
            $"OS={RuntimeInformation.OSDescription}",
            $"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}",
            $"OSArchitecture={RuntimeInformation.OSArchitecture}",
            $"WorkingDirectory={Environment.CurrentDirectory}",
            $"BaseDirectory={AppContext.BaseDirectory}",
            $"StartupLogPath={StartupDiagnostics.StartupLogPath}",
            $"HOME={GetEnvironmentValue(environmentVariables, "HOME")}",
            $"DISPLAY={GetEnvironmentValue(environmentVariables, "DISPLAY")}",
            $"WAYLAND_DISPLAY={GetEnvironmentValue(environmentVariables, "WAYLAND_DISPLAY")}",
            $"GDK_BACKEND={GetEnvironmentValue(environmentVariables, LinuxWebViewEnvironment.GtkBackendEnvironmentVariable)}",
            $"XDG_SESSION_TYPE={GetEnvironmentValue(environmentVariables, "XDG_SESSION_TYPE")}",
            $"XDG_RUNTIME_DIR={GetEnvironmentValue(environmentVariables, "XDG_RUNTIME_DIR")}",
            $"DOTNET_ROOT={GetEnvironmentValue(environmentVariables, "DOTNET_ROOT")}",
            $"DOTNET_SYSTEM_GLOBALIZATION_INVARIANT={GetEnvironmentValue(environmentVariables, "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT")}",
            $"=== end diagnostics ==="
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetEnvironmentValue(IDictionary<string, string?> environmentVariables, string key)
    {
        return environmentVariables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "<not set>";
    }
}
