using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Libreguard.Vpn.Linux.Services;

public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxCapturedOutputChars = 64 * 1024;

    public Task<ProcessResult> StartDetachedAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedFileName = ResolveExecutable(fileName);
        var startInfo = new ProcessStartInfo(resolvedFileName)
        {
            RedirectStandardError = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            return Task.FromResult(process.Start()
                ? new ProcessResult(0, string.Empty, string.Empty)
                : new ProcessResult(127, string.Empty, $"Failed to start {resolvedFileName}."));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return Task.FromResult(new ProcessResult(127, string.Empty, "The requested desktop process could not be started."));
        }
    }

    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var resolvedFileName = ResolveExecutable(fileName);
        var startInfo = new ProcessStartInfo(resolvedFileName)
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendBounded(output, e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendBounded(error, e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                    return new ProcessResult(127, string.Empty, $"Failed to start {resolvedFileName}.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(127, string.Empty, "The requested process could not be started.");
        }
    }

    private static string ResolveExecutable(string fileName)
    {
        if (!OperatingSystem.IsLinux() || Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        return fileName switch
        {
            "ip" => "/usr/sbin/ip",
            "getfacl" => "/usr/bin/getfacl",
            "getenforce" => "/usr/bin/getenforce",
            "journalctl" => "/usr/bin/journalctl",
            "matchpathcon" => "/usr/bin/matchpathcon",
            "nmcli" => "/usr/bin/nmcli",
            "openssl" => "/usr/bin/openssl",
            "pkexec" => "/usr/bin/pkexec",
            "resolvectl" => "/usr/bin/resolvectl",
            "restorecon" => "/usr/bin/restorecon",
            "secret-tool" => "/usr/bin/secret-tool",
            "setfacl" => "/usr/bin/setfacl",
            "xdg-open" => "/usr/bin/xdg-open",
            _ => fileName
        };
    }

    private static void AppendBounded(StringBuilder builder, string line)
    {
        if (builder.Length >= MaxCapturedOutputChars)
        {
            return;
        }

        var remaining = MaxCapturedOutputChars - builder.Length;
        if (line.Length <= remaining)
        {
            builder.AppendLine(line);
            return;
        }

        builder.Append(line.AsSpan(0, Math.Max(0, remaining - 18)));
        builder.AppendLine("...[truncated]");
    }
}
