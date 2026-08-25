using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

/// <summary>
/// Keeps a NetworkManager VPN profile from outliving the desktop process that
/// created it. The guardian runs in a transient user-systemd unit, so killing
/// the desktop process (or its ordinary child process tree) does not suppress
/// the cleanup.
/// </summary>
internal interface IVpnSessionGuardian
{
    Task PrepareConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task StartAsync(VpnProfile profile, CancellationToken cancellationToken);
    Task CompleteAsync(CancellationToken cancellationToken);
}

internal sealed class NullVpnSessionGuardian : IVpnSessionGuardian
{
    public static readonly NullVpnSessionGuardian Instance = new();

    public Task StartAsync(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class VpnSessionGuardian : IVpnSessionGuardian
{
    internal const string GuardianArgument = "--vpn-session-guardian";
    internal const string LeaseArgument = "--lease";
    internal const string NonceArgument = "--nonce";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(100);
    internal const string LifecycleLockFileName = "vpn-lifecycle.lock";

    private readonly IProcessRunner _processRunner;
    private readonly Action<string> _diagnosticSink;
    private readonly string _executablePath;
    private readonly string _runtimeDirectory;
    private FileStream? _lifecycleLock;
    private GuardianLeaseHandle? _activeLease;

    public VpnSessionGuardian(IProcessRunner processRunner)
        : this(processRunner, StartupDiagnostics.Log)
    {
    }

    internal VpnSessionGuardian(
        IProcessRunner processRunner,
        Action<string>? diagnosticSink = null,
        string? executablePath = null,
        string? runtimeDirectory = null)
    {
        _processRunner = processRunner;
        _diagnosticSink = diagnosticSink ?? StartupDiagnostics.Log;
        _executablePath = executablePath ?? Path.Combine(AppContext.BaseDirectory, "libreguard-vpn-linux");
        _runtimeDirectory = runtimeDirectory ?? ResolveRuntimeDirectory();
    }

    public async Task StartAsync(VpnProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!VpnSessionGuardianCommand.IsLibreGuardVpnProfile(profile.ProfileName))
        {
            throw new VpnConfigurationException("LibreGuard refused to create a lifecycle guardian for a profile it does not own.");
        }

        if (_activeLease is not null)
        {
            if (string.Equals(_activeLease.Lease.ProfileName, profile.ProfileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new VpnConfigurationException("LibreGuard must finish the previous VPN lifecycle guardian before starting another connection.");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new VpnConfigurationException("LibreGuard requires Linux systemd user services to protect VPN DNS cleanup.");
        }

        if (_lifecycleLock is null)
        {
            throw new VpnConfigurationException("LibreGuard must acquire the VPN lifecycle lock before installing a guarded connection.");
        }

        var nonce = Guid.NewGuid().ToString("N");
        var leasePath = Path.Combine(_runtimeDirectory, $"vpn-session-{nonce}.json");
        var readyPath = leasePath + ".ready";
        var unitName = $"libreguard-vpn-guardian-{nonce}";
        var connectionUuid = await GetConnectionUuidAsync(profile.ProfileName, cancellationToken)
            ?? throw new VpnConfigurationException("LibreGuard could not read the newly installed NetworkManager profile identity. The VPN was not activated.");
        var lease = new VpnSessionLease(
            profile.ProfileName,
            connectionUuid,
            Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            nonce,
            Completed: false);

        try
        {
            await WriteLeaseAsync(leasePath, lease, cancellationToken);

            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startCts.CancelAfter(StartTimeout);
            var start = await _processRunner.RunAsync("systemd-run", [
                "--user",
                "--collect",
                "--quiet",
                "--no-block",
                "--unit",
                unitName,
                _executablePath,
                GuardianArgument,
                LeaseArgument,
                leasePath,
                NonceArgument,
                nonce
            ], startCts.Token);

            if (!start.Success)
            {
                throw new VpnConfigurationException("LibreGuard could not start the VPN lifecycle guardian. The VPN was not activated.");
            }

            await WaitForReadyAsync(readyPath, nonce, startCts.Token);
            _activeLease = new GuardianLeaseHandle(leasePath, readyPath, unitName, lease);
            _diagnosticSink($"vpn-session-guardian-started profile=\"{Redact(profile.ProfileName)}\" unit=\"{unitName}\"");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(leasePath);
            TryDelete(readyPath);
            ReleaseLifecycleLock();
            throw new VpnConfigurationException("LibreGuard could not verify the VPN lifecycle guardian before activation.");
        }
        catch
        {
            TryDelete(leasePath);
            TryDelete(readyPath);
            ReleaseLifecycleLock();
            throw;
        }
    }

    public async Task PrepareConnectionAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleLock is not null)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new VpnConfigurationException("LibreGuard requires Linux systemd user services to protect VPN DNS cleanup.");
        }

        Directory.CreateDirectory(_runtimeDirectory);
        SetOwnerOnlyMode(_runtimeDirectory, isDirectory: true);
        var lifecycleLock = await TryAcquireLifecycleLockAsync(_runtimeDirectory, StartTimeout, cancellationToken);
        if (lifecycleLock is null)
        {
            throw new VpnConfigurationException("LibreGuard could not acquire the VPN lifecycle lock. Wait a few seconds and try connecting again.");
        }

        _lifecycleLock = lifecycleLock;
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        var activeLease = _activeLease;
        if (activeLease is null)
        {
            ReleaseLifecycleLock();
            return;
        }

        var completedLease = activeLease.Lease with { Completed = true };
        await WriteLeaseAsync(activeLease.LeasePath, completedLease, cancellationToken);
        _activeLease = null;
        ReleaseLifecycleLock();
        _diagnosticSink($"vpn-session-guardian-completed profile=\"{Redact(completedLease.ProfileName)}\" unit=\"{activeLease.UnitName}\"");
    }

    private async Task WaitForReadyAsync(string readyPath, string nonce, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(readyPath))
            {
                try
                {
                    var value = await File.ReadAllTextAsync(readyPath, cancellationToken);
                    if (string.Equals(value.Trim(), nonce, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // The child may be atomically replacing its ready marker.
                }
            }

            await Task.Delay(ReadyPollInterval, cancellationToken);
        }
    }

    internal static async Task WriteLeaseAsync(string path, VpnSessionLease lease, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".new";
        var json = JsonSerializer.Serialize(lease);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        SetOwnerOnlyMode(temporaryPath, isDirectory: false);
        File.Move(temporaryPath, path, overwrite: true);
        SetOwnerOnlyMode(path, isDirectory: false);
    }

    internal static string ResolveRuntimeDirectory()
    {
        var runtimeRoot = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Path.IsPathRooted(runtimeRoot))
        {
            throw new VpnConfigurationException("LibreGuard requires XDG_RUNTIME_DIR to run its VPN lifecycle guardian.");
        }

        return Path.Combine(runtimeRoot, "libreguard-vpn-linux");
    }

    internal static void SetOwnerOnlyMode(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, mode);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static async Task<FileStream?> TryAcquireLifecycleLockAsync(
        string runtimeDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var lockPath = Path.Combine(runtimeDirectory, LifecycleLockFileName);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                SetOwnerOnlyMode(lockPath, isDirectory: false);
#pragma warning disable CA1416 // The method returns before this point on non-Linux platforms.
                stream.Lock(0, 1);
#pragma warning restore CA1416
                return stream;
            }
            catch (IOException)
            {
                stream?.Dispose();
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null;
                }

                await Task.Delay(ReadyPollInterval, cancellationToken);
            }
        }
    }

    private void ReleaseLifecycleLock()
    {
        _lifecycleLock?.Dispose();
        _lifecycleLock = null;
    }

    private static string Redact(string value)
        => value.Replace('"', '_').Replace('\r', '_').Replace('\n', '_');

    private async Task<string?> GetConnectionUuidAsync(string profileName, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "nmcli",
            ["-g", "connection.uuid", "connection", "show", profileName],
            cancellationToken);
        return VpnSessionGuardianCommand.ReadConnectionUuid(profileName, result);
    }

    private sealed record GuardianLeaseHandle(
        string LeasePath,
        string ReadyPath,
        string UnitName,
        VpnSessionLease Lease);
}

internal static class VpnSessionGuardianCommand
{
    private static readonly TimeSpan ParentPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public static bool TryHandle(string[] args, out int exitCode)
    {
        if (!args.Contains(VpnSessionGuardian.GuardianArgument, StringComparer.Ordinal))
        {
            exitCode = 0;
            return false;
        }

        exitCode = RunAsync(args).GetAwaiter().GetResult();
        return true;
    }

    internal static bool IsLibreGuardVpnProfile(string profileName)
        => !string.IsNullOrWhiteSpace(profileName)
            && (profileName.StartsWith("libreguard-openvpn-", StringComparison.OrdinalIgnoreCase)
                || profileName.StartsWith("libreguard-ikev2-", StringComparison.OrdinalIgnoreCase));

    private static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var leasePath, out var nonce))
        {
            Console.Error.WriteLine("Invalid LibreGuard VPN lifecycle guardian arguments.");
            return 64;
        }

        VpnSessionLease lease;
        try
        {
            lease = await ReadLeaseAsync(leasePath);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            StartupDiagnostics.Log($"vpn-session-guardian-invalid-lease type={exception.GetType().Name}");
            return 65;
        }

        if (!IsValidLease(lease, nonce))
        {
            StartupDiagnostics.Log("vpn-session-guardian-invalid-lease reason=validation");
            return 65;
        }

        try
        {
            await File.WriteAllTextAsync(leasePath + ".ready", nonce);
            VpnSessionGuardian.SetOwnerOnlyMode(leasePath + ".ready", isDirectory: false);
            StartupDiagnostics.Log($"vpn-session-guardian-ready profile=\"{Redact(lease.ProfileName)}\" parent_pid={lease.ParentProcessId}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StartupDiagnostics.Log($"vpn-session-guardian-ready-failed type={exception.GetType().Name}");
            return 66;
        }

        while (true)
        {
            VpnSessionLease currentLease;
            try
            {
                currentLease = await ReadLeaseAsync(leasePath);
            }
            catch (FileNotFoundException)
            {
                return await CleanUpUnexpectedExitAsync(lease.ProfileName, lease.ConnectionUuid, leasePath);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                StartupDiagnostics.Log($"vpn-session-guardian-lease-read-failed type={exception.GetType().Name}");
                return 67;
            }

            if (!IsValidLease(currentLease, nonce))
            {
                StartupDiagnostics.Log("vpn-session-guardian-lease-read-failed reason=validation");
                return 67;
            }

            if (currentLease.Completed)
            {
                VpnSessionGuardian.TryDelete(leasePath);
                VpnSessionGuardian.TryDelete(leasePath + ".ready");
                return 0;
            }

            if (!IsParentAlive(currentLease.ParentProcessId, currentLease.ParentStartUtcTicks))
            {
                StartupDiagnostics.Log($"vpn-session-guardian-parent-exited profile=\"{Redact(currentLease.ProfileName)}\" parent_pid={currentLease.ParentProcessId}");
                return await CleanUpUnexpectedExitAsync(currentLease.ProfileName, currentLease.ConnectionUuid, leasePath);
            }

            await Task.Delay(ParentPollInterval);
        }
    }

    private static async Task<int> CleanUpUnexpectedExitAsync(
        string profileName,
        string connectionUuid,
        string leasePath)
    {
        using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
        try
        {
            var runtimeDirectory = Path.GetDirectoryName(leasePath);
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                return 68;
            }

            using var lifecycleLock = await VpnSessionGuardian.TryAcquireLifecycleLockAsync(
                runtimeDirectory,
                CleanupTimeout,
                cleanupCts.Token);
            if (lifecycleLock is null)
            {
                VpnSessionGuardian.TryDelete(leasePath);
                VpnSessionGuardian.TryDelete(leasePath + ".ready");
                StartupDiagnostics.Log($"vpn-session-guardian-cleanup-skipped profile=\"{Redact(profileName)}\" reason=lifecycle-lock-busy");
                return 0;
            }

            var uuidResult = await new ProcessRunner().RunAsync(
                "nmcli",
                ["-g", "connection.uuid", "connection", "show", profileName],
                cleanupCts.Token);
            var currentConnectionUuid = ReadConnectionUuid(profileName, uuidResult);
            return await CleanUpUnexpectedExitAsync(
                profileName,
                connectionUuid,
                currentConnectionUuid,
                leasePath,
                new NetworkManagerClient(new ProcessRunner()),
                cleanupCts.Token);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Log($"vpn-session-guardian-cleanup-failed profile=\"{Redact(profileName)}\" type={exception.GetType().Name}");
            return 68;
        }
    }

    internal static async Task<int> CleanUpUnexpectedExitAsync(
        string profileName,
        string expectedConnectionUuid,
        string? currentConnectionUuid,
        string leasePath,
        INetworkManagerClient networkManager,
        CancellationToken cancellationToken)
    {
        if (!IsLibreGuardVpnProfile(profileName)
            || !IsCanonicalUuid(expectedConnectionUuid))
        {
            StartupDiagnostics.Log("vpn-session-guardian-cleanup-refused reason=unowned-profile");
            return 65;
        }

        ArgumentNullException.ThrowIfNull(networkManager);
        if (!string.Equals(expectedConnectionUuid, currentConnectionUuid, StringComparison.OrdinalIgnoreCase))
        {
            VpnSessionGuardian.TryDelete(leasePath);
            VpnSessionGuardian.TryDelete(leasePath + ".ready");
            StartupDiagnostics.Log($"vpn-session-guardian-cleanup-skipped profile=\"{Redact(profileName)}\" reason=profile-instance-changed");
            return 0;
        }

        StartupDiagnostics.Log($"vpn-session-guardian-cleanup-begin profile=\"{Redact(profileName)}\"");
        try
        {
            await networkManager.EnsureAvailableAsync(cancellationToken);
            await networkManager.DeactivateAsync(profileName, cancellationToken);
            await networkManager.DeleteLibreGuardProfileAsync(profileName, cancellationToken);
            await networkManager.CleanupLibreGuardProfileArtifactsAsync(profileName, cancellationToken);
            VpnSessionGuardian.TryDelete(leasePath);
            VpnSessionGuardian.TryDelete(leasePath + ".ready");
            StartupDiagnostics.Log($"vpn-session-guardian-cleanup-complete profile=\"{Redact(profileName)}\"");
            return 0;
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Log($"vpn-session-guardian-cleanup-failed profile=\"{Redact(profileName)}\" type={exception.GetType().Name}");
            return 68;
        }
    }

    internal static bool IsParentAlive(int processId, long expectedStartUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited
                && process.StartTime.ToUniversalTime().Ticks == expectedStartUtcTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool TryParseArguments(string[] args, out string leasePath, out string nonce)
    {
        leasePath = string.Empty;
        nonce = string.Empty;
        if (args.Length != 5
            || !string.Equals(args[0], VpnSessionGuardian.GuardianArgument, StringComparison.Ordinal)
            || !string.Equals(args[1], VpnSessionGuardian.LeaseArgument, StringComparison.Ordinal)
            || !string.Equals(args[3], VpnSessionGuardian.NonceArgument, StringComparison.Ordinal))
        {
            return false;
        }

        leasePath = args[2];
        nonce = args[4];
        return Path.IsPathRooted(leasePath)
            && Guid.TryParseExact(nonce, "N", out _);
    }

    private static async Task<VpnSessionLease> ReadLeaseAsync(string path)
        => JsonSerializer.Deserialize<VpnSessionLease>(await File.ReadAllTextAsync(path))
            ?? throw new JsonException("The VPN lifecycle lease is empty.");

    private static bool IsValidLease(VpnSessionLease lease, string nonce)
        => IsLibreGuardVpnProfile(lease.ProfileName)
            && IsCanonicalUuid(lease.ConnectionUuid)
            && lease.ParentProcessId > 0
            && lease.ParentStartUtcTicks > 0
            && string.Equals(lease.Nonce, nonce, StringComparison.Ordinal)
            && Guid.TryParseExact(lease.Nonce, "N", out _);

    internal static string? ReadConnectionUuid(string profileName, ProcessResult result)
    {
        if (!result.Success)
        {
            if (Regex.IsMatch(result.StandardError, "unknown|not found", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return null;
            }

            throw new VpnConfigurationException($"NetworkManager could not read the profile identity for {profileName}.");
        }

        var uuid = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!IsCanonicalUuid(uuid))
        {
            throw new VpnConfigurationException($"NetworkManager returned an invalid profile identity for {profileName}.");
        }

        return Guid.Parse(uuid!).ToString("D");
    }

    private static bool IsCanonicalUuid(string? value)
        => !string.IsNullOrWhiteSpace(value) && Guid.TryParseExact(value, "D", out _);

    private static string Redact(string value)
        => value.Replace('"', '_').Replace('\r', '_').Replace('\n', '_');
}

internal sealed record VpnSessionLease(
    string ProfileName,
    string ConnectionUuid,
    int ParentProcessId,
    long ParentStartUtcTicks,
    string Nonce,
    bool Completed);
