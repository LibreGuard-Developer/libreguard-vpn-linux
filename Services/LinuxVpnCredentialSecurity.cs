namespace Libreguard.Vpn.Linux.Services;

internal static class LinuxVpnCredentialSecurity
{
    private const string Enforcing = "Enforcing";
    private const string Permissive = "Permissive";

    public static Task EnsureReadyAsync(
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
        => EnsureReadyAsync(
            processRunner,
            XdgPaths.VpnCredentialDirectory,
            OperatingSystem.IsLinux(),
            cancellationToken);

    public static Task EnsureReadyAsync(
        IProcessRunner processRunner,
        string credentialDirectory,
        CancellationToken cancellationToken)
        => EnsureReadyAsync(
            processRunner,
            credentialDirectory,
            OperatingSystem.IsLinux(),
            cancellationToken);

    internal static async Task EnsureReadyAsync(
        IProcessRunner processRunner,
        string credentialDirectory,
        bool isLinux,
        CancellationToken cancellationToken)
    {
        if (!isLinux)
        {
            return;
        }

        var enforcement = await processRunner.RunAsync("getenforce", [], cancellationToken);
        var mode = enforcement.Success ? enforcement.StandardOutput.Trim() : string.Empty;
        if (!mode.Equals(Enforcing, StringComparison.OrdinalIgnoreCase)
            && !mode.Equals(Permissive, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var restore = await processRunner.RunAsync("restorecon", ["-RF", credentialDirectory], cancellationToken);
        if (!restore.Success)
        {
            HandleFailure(mode, credentialDirectory, "restore the Fedora SELinux context", restore.StandardError);
            return;
        }

        var paths = new List<string> { credentialDirectory };
        if (Directory.Exists(credentialDirectory))
        {
            paths.AddRange(Directory.EnumerateFileSystemEntries(
                credentialDirectory,
                "*",
                SearchOption.TopDirectoryOnly));
        }

        foreach (var path in paths)
        {
            var verify = await processRunner.RunAsync("matchpathcon", ["-V", path], cancellationToken);
            if (!verify.Success)
            {
                HandleFailure(
                    mode,
                    credentialDirectory,
                    $"verify the Fedora SELinux context for '{path}'",
                    verify.StandardError);
                return;
            }
        }
    }

    private static void HandleFailure(
        string mode,
        string credentialDirectory,
        string operation,
        string standardError)
    {
        var detail = string.IsNullOrWhiteSpace(standardError)
            ? string.Empty
            : $" {standardError.Trim()}";
        if (mode.Equals(Enforcing, StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnConfigurationException(
                $"LibreGuard could not {operation}. Install policycoreutils and libselinux-utils, then restore the default context for " +
                $"'{credentialDirectory}' before connecting.{detail}");
        }

        StartupDiagnostics.Log($"vpn-credential-selinux-warning operation={Sanitize(operation)}");
    }

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '-').Replace('"', '\'');
}
