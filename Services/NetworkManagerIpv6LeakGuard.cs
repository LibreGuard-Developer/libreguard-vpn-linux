using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

/// <summary>
/// Temporarily removes the physical uplink's IPv6 default route while a VPN that only
/// supplies IPv4 is active. The original NetworkManager setting is persisted before it
/// is changed so an interrupted client can repair the uplink on its next startup.
/// </summary>
internal sealed class NetworkManagerIpv6LeakGuard
{
    private const string Ipv6ProbeAddress = "2606:4700:4700::1111";

    private readonly IProcessRunner _processRunner;
    private readonly Action<string> _diagnosticSink;
    private readonly string _statePath;
    private Ipv6LeakGuardState? _activeState;
    private string? _profileWithNoPhysicalIpv6Route;

    public NetworkManagerIpv6LeakGuard(
        IProcessRunner processRunner,
        Action<string> diagnosticSink,
        string? statePath = null)
    {
        _processRunner = processRunner;
        _diagnosticSink = diagnosticSink;
        _statePath = statePath ?? Path.Combine(XdgPaths.AppStateDirectory, "ipv6-leak-guard.json");
    }

    public async Task RestoreStaleStateAsync(CancellationToken cancellationToken)
    {
        if (_activeState is not null)
        {
            return;
        }

        var state = await ReadStateAsync(cancellationToken);
        if (state is null)
        {
            return;
        }

        if (await IsProfileActiveAsync(state.ProfileName, cancellationToken))
        {
            _activeState = state;
            _diagnosticSink($"vpn-ipv6-guard-recovery-deferred profile=\"{Redact(state.ProfileName)}\" reason=active-profile");
            return;
        }

        await RestoreAsync(state, cancellationToken);
    }

    public async Task EngageAsync(VpnProfile profile, CancellationToken cancellationToken)
    {
        if (_activeState is not null)
        {
            if (string.Equals(_activeState.ProfileName, profile.ProfileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new VpnConfigurationException("LibreGuard must restore the previous IPv6 leak guard before activating another VPN profile.");
        }

        var outerTransportAddress = ValidateOuterTransportAddress(profile.OuterTransportAddress);
        await VerifyOuterTransportRouteAsync(outerTransportAddress, cancellationToken);

        var ipv6Route = await _processRunner.RunAsync("ip", ["-6", "route", "get", Ipv6ProbeAddress], cancellationToken);
        if (IsNoRoute(ipv6Route))
        {
            _profileWithNoPhysicalIpv6Route = profile.ProfileName;
            _diagnosticSink($"vpn-ipv6-guard-not-needed profile=\"{Redact(profile.ProfileName)}\" reason=no-physical-ipv6-default-route");
            return;
        }

        if (!ipv6Route.Success)
        {
            throw new VpnConfigurationException("LibreGuard could not determine the physical IPv6 route before activating the VPN; refusing to risk an IPv6 leak.");
        }

        var deviceName = ExtractRouteDevice(ipv6Route.StandardOutput);
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new VpnConfigurationException("LibreGuard could not identify the physical IPv6 device before activating the VPN; refusing to risk an IPv6 leak.");
        }

        var connectionName = await QueryDevicePropertyAsync(deviceName, "GENERAL.CONNECTION", cancellationToken);
        var deviceType = await QueryDevicePropertyAsync(deviceName, "GENERAL.TYPE", cancellationToken);
        if (string.IsNullOrWhiteSpace(connectionName)
            || string.Equals(connectionName, "--", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceType, "vpn", StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnConfigurationException("LibreGuard could not safely identify the physical NetworkManager connection that owns IPv6 routing.");
        }

        var connectionUuid = await QueryConnectionPropertyAsync(connectionName, "connection.uuid", cancellationToken);
        var originalNeverDefault = NormalizeBoolean(await QueryConnectionPropertyAsync(connectionName, "ipv6.never-default", cancellationToken));
        if (string.IsNullOrWhiteSpace(connectionUuid) || originalNeverDefault is null)
        {
            throw new VpnConfigurationException("LibreGuard could not snapshot the physical IPv6 routing setting; refusing to risk an IPv6 leak.");
        }

        if (originalNeverDefault.Value)
        {
            throw new VpnConfigurationException("The physical connection still exposes an IPv6 default route despite its NetworkManager setting. LibreGuard cannot safely contain IPv6 traffic.");
        }

        var state = new Ipv6LeakGuardState(profile.ProfileName, connectionName, connectionUuid, deviceName, originalNeverDefault.Value);
        await WriteStateAsync(state, cancellationToken);

        try
        {
            await ModifyAndReapplyAsync(connectionName, deviceName, "yes", cancellationToken);
            var verify = await _processRunner.RunAsync("ip", ["-6", "route", "get", Ipv6ProbeAddress], cancellationToken);
            if (!IsNoRoute(verify))
            {
                throw new VpnConfigurationException("NetworkManager did not remove the physical IPv6 default route; refusing to activate a tunnel that could leak IPv6 traffic.");
            }

            _activeState = state;
            _diagnosticSink($"vpn-ipv6-guard-engaged profile=\"{Redact(profile.ProfileName)}\" device=\"{Redact(deviceName)}\"");
        }
        catch
        {
            try
            {
                await RestoreAsync(state, CancellationToken.None);
            }
            catch (Exception restoreException)
            {
                _diagnosticSink($"vpn-ipv6-guard-restore-failed profile=\"{Redact(profile.ProfileName)}\" error=\"{Redact(restoreException.Message)}\"");
            }

            throw;
        }
    }

    public async Task VerifyAfterActivationAsync(string profileName, string vpnDeviceName, CancellationToken cancellationToken)
    {
        var route = await _processRunner.RunAsync("ip", ["-6", "route", "get", Ipv6ProbeAddress], cancellationToken);
        if (route.Success)
        {
            if (RouteUsesDevice(route.StandardOutput, vpnDeviceName))
            {
                return;
            }

            _diagnosticSink($"vpn-route-verification-failed profile=\"{Redact(profileName)}\" target=\"{Ipv6ProbeAddress}\" expected_device=\"{Redact(vpnDeviceName)}\" route=\"{Redact(route.StandardOutput)}\"");
            throw new VpnConfigurationException($"The IPv6 full-tunnel traffic route for {profileName} does not use VPN device {vpnDeviceName}; refusing to enable traffic.");
        }

        if (IsKernelRouteRejection(route) && IsContainedFor(profileName))
        {
            _diagnosticSink(
                $"vpn-ipv6-fallback-contained profile=\"{Redact(profileName)}\" "
                + $"reason=kernel-route-rejected exit_code={route.ExitCode} "
                + $"stdout=\"{Redact(route.StandardOutput)}\" stderr=\"{Redact(route.StandardError)}\"");
            return;
        }

        _diagnosticSink(
            $"vpn-ipv6-verification-failed profile=\"{Redact(profileName)}\" "
            + $"exit_code={route.ExitCode} contained={IsContainedFor(profileName).ToString().ToLowerInvariant()} "
            + $"stdout=\"{Redact(route.StandardOutput)}\" stderr=\"{Redact(route.StandardError)}\"");
        throw new VpnConfigurationException($"LibreGuard could not verify safe IPv6 routing for {profileName}; refusing to enable traffic.");
    }

    public async Task RestoreForProfileAsync(string profileName, CancellationToken cancellationToken)
    {
        if (string.Equals(_profileWithNoPhysicalIpv6Route, profileName, StringComparison.OrdinalIgnoreCase))
        {
            _profileWithNoPhysicalIpv6Route = null;
        }

        var state = _activeState;
        if (state is null)
        {
            state = await ReadStateAsync(cancellationToken);
        }

        if (state is null || !string.Equals(state.ProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await RestoreAsync(state, cancellationToken);
    }

    private async Task RestoreAsync(Ipv6LeakGuardState state, CancellationToken cancellationToken)
    {
        var currentUuid = await QueryConnectionPropertyAsync(state.ConnectionName, "connection.uuid", cancellationToken);
        if (!string.Equals(currentUuid, state.ConnectionUuid, StringComparison.OrdinalIgnoreCase))
        {
            _diagnosticSink($"vpn-ipv6-guard-restore-skipped profile=\"{Redact(state.ProfileName)}\" reason=connection-changed");
            ClearState();
            _activeState = null;
            return;
        }

        var currentNeverDefault = NormalizeBoolean(await QueryConnectionPropertyAsync(state.ConnectionName, "ipv6.never-default", cancellationToken));
        if (currentNeverDefault is null)
        {
            throw new VpnConfigurationException("LibreGuard could not read the physical IPv6 setting while restoring the network state.");
        }

        if (currentNeverDefault.Value)
        {
            await ModifyAndReapplyAsync(state.ConnectionName, state.DeviceName, state.OriginalNeverDefault ? "yes" : "no", cancellationToken);
        }
        else if (state.OriginalNeverDefault)
        {
            throw new VpnConfigurationException("The physical IPv6 setting changed while LibreGuard was active; refusing to overwrite it during restoration.");
        }

        ClearState();
        _activeState = null;
        _diagnosticSink($"vpn-ipv6-guard-restored profile=\"{Redact(state.ProfileName)}\" device=\"{Redact(state.DeviceName)}\"");
    }

    private async Task VerifyOuterTransportRouteAsync(string outerTransportAddress, CancellationToken cancellationToken)
    {
        var route = await _processRunner.RunAsync("ip", ["-4", "route", "get", outerTransportAddress], cancellationToken);
        var deviceName = ExtractRouteDevice(route.StandardOutput);
        if (!route.Success || string.IsNullOrWhiteSpace(deviceName))
        {
            throw new VpnConfigurationException("LibreGuard could not verify the IPv4 route to the VPN server before applying IPv6 leak containment.");
        }

        var deviceType = await QueryDevicePropertyAsync(deviceName, "GENERAL.TYPE", cancellationToken);
        if (string.Equals(deviceType, "vpn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceType, "wireguard", StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnConfigurationException("The VPN server's outer IPv4 transport route is not on a physical NetworkManager device; refusing to risk recursive or leaked VPN traffic.");
        }
    }

    private async Task<string> QueryDevicePropertyAsync(string deviceName, string property, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("nmcli", ["-g", property, "device", "show", deviceName], cancellationToken);
        if (!result.Success)
        {
            throw new VpnConfigurationException($"LibreGuard could not inspect NetworkManager device property '{property}'.");
        }

        return NormalizeNmcliValue(result.StandardOutput);
    }

    private async Task<string> QueryConnectionPropertyAsync(string connectionName, string property, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("nmcli", ["-g", property, "connection", "show", connectionName], cancellationToken);
        if (!result.Success)
        {
            throw new VpnConfigurationException($"LibreGuard could not inspect NetworkManager connection property '{property}'.");
        }

        return NormalizeNmcliValue(result.StandardOutput);
    }

    private async Task ModifyAndReapplyAsync(string connectionName, string deviceName, string neverDefault, CancellationToken cancellationToken)
    {
        var modify = await _processRunner.RunAsync("nmcli", ["connection", "modify", connectionName, "ipv6.never-default", neverDefault], cancellationToken);
        if (!modify.Success)
        {
            throw new VpnConfigurationException("NetworkManager refused to update the physical IPv6 default-route setting.");
        }

        var reapply = await _processRunner.RunAsync("nmcli", ["device", "reapply", deviceName], cancellationToken);
        if (!reapply.Success)
        {
            throw new VpnConfigurationException("NetworkManager could not apply the physical IPv6 default-route setting.");
        }
    }

    private async Task<bool> IsProfileActiveAsync(string profileName, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("nmcli", ["-t", "-f", "NAME,TYPE", "connection", "show", "--active"], cancellationToken);
        if (!result.Success)
        {
            throw new VpnConfigurationException("LibreGuard could not inspect active NetworkManager profiles while recovering IPv6 routing.");
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.LastIndexOf(':') is var index && index > 0 ? line[..index] : string.Empty)
            .Any(name => string.Equals(name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Ipv6LeakGuardState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        FileSecurity.EnsureNotSymbolicLink(_statePath);
        FileSecurity.EnsurePrivateFile(_statePath);
        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync<Ipv6LeakGuardState>(stream, JsonOptions.Default, cancellationToken);
        if (state is null
            || string.IsNullOrWhiteSpace(state.ProfileName)
            || string.IsNullOrWhiteSpace(state.ConnectionName)
            || string.IsNullOrWhiteSpace(state.ConnectionUuid)
            || string.IsNullOrWhiteSpace(state.DeviceName))
        {
            throw new VpnConfigurationException("LibreGuard found an invalid pending IPv6 recovery record. Restore the physical IPv6 setting manually before connecting again.");
        }

        return state;
    }

    private async Task WriteStateAsync(Ipv6LeakGuardState state, CancellationToken cancellationToken)
    {
        XdgPaths.EnsureAppDirectories();
        var directory = Path.GetDirectoryName(_statePath) ?? XdgPaths.AppStateDirectory;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = FileSecurity.CreatePrivateFile(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions.Default, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            FileSecurity.EnsureNotSymbolicLink(_statePath);
            File.Move(temporaryPath, _statePath, overwrite: true);
            FileSecurity.EnsurePrivateFile(_statePath);
        }
        catch
        {
            FileSecurity.TryDelete(temporaryPath);
            throw;
        }
    }

    private void ClearState()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        FileSecurity.EnsureNotSymbolicLink(_statePath);
        File.Delete(_statePath);
    }

    private bool IsEngagedFor(string profileName)
        => _activeState is not null
            && string.Equals(_activeState.ProfileName, profileName, StringComparison.OrdinalIgnoreCase);

    private bool IsContainedFor(string profileName)
        => IsEngagedFor(profileName)
            || string.Equals(_profileWithNoPhysicalIpv6Route, profileName, StringComparison.OrdinalIgnoreCase);

    private static string ValidateOuterTransportAddress(string? value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new VpnConfigurationException("LibreGuard requires a literal IPv4 server address to establish an IPv4-only tunnel without exposing IPv6 traffic.");
        }

        return address.ToString();
    }

    private static bool IsNoRoute(ProcessResult result)
        => !result.Success
            && Regex.IsMatch(result.StandardError + " " + result.StandardOutput, "network is unreachable|no route to host|unreachable", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsKernelRouteRejection(ProcessResult result)
        // iproute2 returns 1 or 2 when the kernel declines a route lookup.
        // Exit 127 is reserved by ProcessRunner for a command-start failure and
        // must never be treated as proof of containment.
        => !result.Success && result.ExitCode is 1 or 2;

    private static string? ExtractRouteDevice(string routeOutput)
    {
        var tokens = routeOutput.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (string.Equals(tokens[index], "dev", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(tokens[index + 1]))
            {
                return tokens[index + 1];
            }
        }

        return null;
    }

    private static bool RouteUsesDevice(string routeOutput, string expectedDevice)
        => string.Equals(ExtractRouteDevice(routeOutput), expectedDevice, StringComparison.Ordinal);

    private static string NormalizeNmcliValue(string value)
    {
        var normalized = value.Trim();
        return string.Equals(normalized, "--", StringComparison.Ordinal) ? string.Empty : normalized;
    }

    private static bool? NormalizeBoolean(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null
        };

    private static string Redact(string value)
        => Regex.Replace(value ?? string.Empty, "[\\r\\n\\t ]+", " ", RegexOptions.CultureInvariant).Trim();

    private sealed record Ipv6LeakGuardState(
        string ProfileName,
        string ConnectionName,
        string ConnectionUuid,
        string DeviceName,
        bool OriginalNeverDefault);
}
