using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class NetworkManagerClient : INetworkManagerClient
{
    private const string OsReleasePath = "/etc/os-release";
    private const string RouteRepairHelperPath = "/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair";
    private const string SystemPreUpDispatcherPath = "/etc/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle";
    private const string VendorPreUpDispatcherPath = "/usr/lib/NetworkManager/dispatcher.d/pre-up.d/90-libreguard-vpn-lifecycle";
    private const string VendorDispatcherPath = "/usr/lib/NetworkManager/dispatcher.d/90-libreguard-vpn-lifecycle";
    private const int InstalledRouteRepairWaitAttempts = 5;
    private static readonly TimeSpan InstalledRouteRepairPollInterval = TimeSpan.FromMilliseconds(200);
    private const string PrivateDnsAddress = "10.254.0.53";
    private const string SystemHostsPath = "/etc/hosts";
    private const string ManagedDohCanaryLine = "0.0.0.0 use-application-dns.net # LibreGuard VPN DoH canary";
    private const int RoutedDnsMinimumMajor = 1;
    private const int RoutedDnsMinimumMinor = 52;
    private static readonly (string Name, string Value)[] OpenVpnFullTunnelSettings =
    [
        ("ipv4.never-default", "no"),
        ("ipv4.ignore-auto-routes", "no"),
        ("ipv6.never-default", "no"),
        ("ipv6.ignore-auto-routes", "no")
    ];
    private static readonly (string Name, string Value)[] IkeV2FullTunnelSettings =
    [
        ("ipv4.never-default", "no"),
        ("ipv4.ignore-auto-routes", "no"),
        ("ipv6.never-default", "yes"),
        ("ipv6.ignore-auto-routes", "yes")
    ];
    private static readonly string[] LibreGuardProfilePrefixes = ["libreguard-openvpn-", "libreguard-ikev2-"];
    private static readonly (string Name, string Value)[] PrivateDnsSettings =
    [
        ("ipv4.dns", PrivateDnsAddress),
        ("ipv4.dns-search", "~."),
        ("ipv4.ignore-auto-dns", "yes"),
        ("ipv4.dns-priority", "-2147483648"),
        ("ipv6.dns", string.Empty),
        ("ipv6.dns-search", string.Empty),
        ("ipv6.ignore-auto-dns", "yes"),
        ("ipv6.dns-priority", "-2147483648")
    ];

    private readonly IProcessRunner _processRunner;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _readAllText;
    private readonly bool _verifyBrowserDohProtection;
    private readonly Action<string> _diagnosticSink;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _userHomeDirectory;
    private readonly ISettingsStore? _settingsStore;
    private readonly NetworkManagerIpv6LeakGuard _ipv6LeakGuard;
    private bool _supportsRoutedDns;
    private Version? _networkManagerVersionValue;
    private string _networkManagerVersion = "unknown";

    public NetworkManagerClient(IProcessRunner processRunner)
        : this(processRunner, File.Exists, StartupDiagnostics.Log)
    {
    }

    public NetworkManagerClient(
        IProcessRunner processRunner,
        Func<string, bool> fileExists,
        Action<string>? diagnosticSink = null,
        string? ipv6LeakGuardStatePath = null,
        Func<string, string>? readAllText = null,
        bool? verifyBrowserDohProtection = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? userHomeDirectory = null,
        ISettingsStore? settingsStore = null)
    {
        _processRunner = processRunner;
        _fileExists = fileExists;
        _readAllText = readAllText ?? File.ReadAllText;
        _verifyBrowserDohProtection = verifyBrowserDohProtection ?? false;
        _diagnosticSink = diagnosticSink ?? StartupDiagnostics.Log;
        _delay = delay ?? Task.Delay;
        _userHomeDirectory = userHomeDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _settingsStore = settingsStore;
        _ipv6LeakGuard = new NetworkManagerIpv6LeakGuard(processRunner, _diagnosticSink, ipv6LeakGuardStatePath);
    }

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        var nmcli = await _processRunner.RunAsync("nmcli", ["--version"], cancellationToken);
        if (!nmcli.Success)
        {
            LogCommandFailure("preflight", null, "nmcli --version", nmcli);
            throw new VpnConfigurationException("NetworkManager nmcli is not available. Install NetworkManager and try again.");
        }

        if (!TryParseNetworkManagerVersion(nmcli.StandardOutput, out var version))
        {
            _diagnosticSink("vpn-networkmanager-preflight-failed reason=unrecognized-version");
            throw new VpnConfigurationException("Could not determine the installed NetworkManager version. Update NetworkManager and try again.");
        }

        _supportsRoutedDns = version >= new Version(RoutedDnsMinimumMajor, RoutedDnsMinimumMinor);
        _networkManagerVersionValue = version;
        _networkManagerVersion = version.ToString();
        _diagnosticSink($"vpn-networkmanager-preflight version={version} routed_dns={(_supportsRoutedDns ? "supported" : "unsupported-private-route-fallback")}");
        await _ipv6LeakGuard.RestoreStaleStateAsync(cancellationToken);
    }

    public async Task ImportOpenVpnAsync(VpnProfile profile, CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken);
        _ = await _processRunner.RunAsync("nmcli", ["connection", "delete", profile.ProfileName], cancellationToken);

        var import = await _processRunner.RunAsync("nmcli", [
            "connection",
            "import",
            "type",
            "openvpn",
            "file",
            profile.ConfigPath
        ], cancellationToken);

        if (!import.Success)
        {
            LogCommandFailure("openvpn-profile-import", profile.ProfileName, "connection.import", import);
            throw new VpnConfigurationException(
                "NetworkManager OpenVPN plugin failed to import the profile. Install network-manager-openvpn on Debian/Ubuntu or NetworkManager-openvpn on Fedora.");
        }

        var importedName = ParseImportedConnectionName(import.StandardOutput)
            ?? ParseImportedConnectionName(import.StandardError)
            ?? profile.ProfileName;
        try
        {
            await ConfigureProfileAsync(importedName, profile.ProfileName, vpnData: null, OpenVpnFullTunnelSettings, cancellationToken);
            await VerifyConfiguredProfileAsync(profile.ProfileName, expectedVpnData: null, profile.OuterTransportAddress, OpenVpnFullTunnelSettings, cancellationToken);
        }
        catch
        {
            await TryDeleteProvisionedProfilesAsync([profile.ProfileName, importedName]);
            throw;
        }
    }

    public async Task ImportIkeV2Async(VpnProfile profile, CancellationToken cancellationToken)
    {
        await EnsureAvailableAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.NetworkManagerVpnData))
        {
            throw new VpnConfigurationException("IKEv2 profile did not produce NetworkManager strongSwan data.");
        }

        var vpnDataItems = SplitVpnDataItems(profile.NetworkManagerVpnData);
        ValidateIkeV2VpnData(vpnDataItems);
        var expectedRemoteAddress = profile.Ikev2RemoteAddress ?? profile.OuterTransportAddress;
        ValidateIkeV2RemoteAddress(vpnDataItems, expectedRemoteAddress);

        var delete = await _processRunner.RunAsync("nmcli", ["connection", "delete", profile.ProfileName], cancellationToken);
        _ = delete;
        var interfaceName = CreateIkeV2InterfaceName();

        var add = await _processRunner.RunAsync("nmcli", [
            "connection",
            "add",
            "type",
            "vpn",
            "con-name",
            profile.ProfileName,
            "ifname",
            interfaceName,
            "vpn-type",
            "strongswan"
        ], cancellationToken);

        if (!add.Success)
        {
            LogCommandFailure("ikev2-profile-add", profile.ProfileName, "connection.add", add);
            throw new VpnConfigurationException("NetworkManager strongSwan plugin is unavailable. Install network-manager-strongswan and strongswan-nm.");
        }

        try
        {
            // NetworkManager 1.56+ lets the strongSwan plugin safely read credentials
            // from the connection owner's private directory. Without this ownership,
            // SELinux confines the plugin and it cannot read LibreGuard's 0600 files.
            await ConfigureProfileAsync(
                profile.ProfileName,
                profile.ProfileName,
                profile.NetworkManagerVpnData,
                IkeV2FullTunnelSettings,
                cancellationToken,
                GetCurrentUserConnectionPermission());
            await VerifyConfiguredProfileAsync(profile.ProfileName, profile.NetworkManagerVpnData, expectedRemoteAddress, IkeV2FullTunnelSettings, cancellationToken);
        }
        catch
        {
            await TryDeleteProvisionedProfilesAsync([profile.ProfileName]);
            throw;
        }
    }

    public async Task ActivateAsync(VpnProfile profile, CancellationToken cancellationToken)
    {
        await _ipv6LeakGuard.EngageAsync(profile, cancellationToken);
        try
        {
            if (profile.Protocol == VpnProtocol.Ikev2
                && profile.Ikev2GatewayCertificatePaths is { Count: > 0 })
            {
                LogGatewayCertificateAttempt(profile, 1, profile.Ikev2GatewayCertificatePaths[0], "initial");
            }

            var useFedoraCredentialHelperWorkaround = profile.Protocol == VpnProtocol.Ikev2
                && RequiresFedoraNetworkManager156CredentialWorkaround();
            IReadOnlyList<PosixAclSnapshot>? fedoraCredentialAclSnapshots = null;
            if (useFedoraCredentialHelperWorkaround)
            {
                fedoraCredentialAclSnapshots = await GrantFedoraIkeV2CredentialAccessAsync(
                    profile,
                    cancellationToken);
                try
                {
                    await SetIkeV2ConnectionPermissionAsync(
                        profile.ProfileName,
                        string.Empty,
                        "fedora-credential-helper-workaround-enable",
                        cancellationToken);
                }
                catch
                {
                    await RestorePosixAclsAsync(
                        profile.ProfileName,
                        fedoraCredentialAclSnapshots,
                        CancellationToken.None);
                    throw;
                }

                _diagnosticSink($"vpn-ikev2-fedora-credential-helper-workaround profile=\"{RedactDiagnostic(profile.ProfileName)}\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\" credential_acl=uid0-temporary state=enabled");
            }

            try
            {
                var up = await _processRunner.RunAsync("nmcli", ["connection", "up", profile.ProfileName], cancellationToken);
                if (up.Success)
                {
                    await RememberSuccessfulIkeV2GatewayRootAsync(
                        profile,
                        profile.Ikev2GatewayCertificatePaths?.FirstOrDefault());
                }
                else
                {
                    var failure = await CaptureActivationFailureAsync(profile.ProfileName, up, cancellationToken);
                    LogCommandFailure("activation", profile.ProfileName, "connection.up", up);

                    if (profile.Protocol == VpnProtocol.Ikev2
                        && ShouldRetryIkeV2WithGatewayCertificate(profile, failure)
                        && profile.Ikev2GatewayCertificatePaths is { Count: > 1 })
                    {
                        var certificateFailure = await TryIkeV2GatewayCertificateFallbackAsync(profile, failure, cancellationToken);
                        if (certificateFailure is not null)
                        {
                            throw CreateActivationFailure(profile.ProfileName, certificateFailure);
                        }

                        up = new ProcessResult(0, string.Empty, string.Empty);
                    }

                    if (!up.Success)
                    {
                        throw CreateActivationFailure(profile.ProfileName, failure);
                    }
                }
            }
            finally
            {
                if (useFedoraCredentialHelperWorkaround)
                {
                    await RestoreFedoraIkeV2CredentialWorkaroundAsync(
                        profile.ProfileName,
                        fedoraCredentialAclSnapshots!,
                        CancellationToken.None);
                    _diagnosticSink($"vpn-ikev2-fedora-credential-helper-workaround profile=\"{RedactDiagnostic(profile.ProfileName)}\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\" credential_acl=removed state=restored-private");
                }
            }

            if (profile.Protocol == VpnProtocol.Ikev2)
            {
                await RepairIkeV2RoutingRuleAsync(cancellationToken);
            }

            await VerifyFullTunnelRoutingAsync(profile.ProfileName, cancellationToken);
        }
        catch
        {
            await TryDeactivateAndRestoreAfterActivationFailureAsync(profile.ProfileName);
            throw;
        }
    }

    public async Task DeactivateAsync(string profileName, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            var down = await _processRunner.RunAsync("nmcli", ["connection", "down", profileName], cancellationToken);
            if (!down.Success
                && !Regex.IsMatch(
                    down.StandardError,
                    "not\\s+(?:an\\s+)?active|unknown|not found",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                failure = new VpnConfigurationException($"NetworkManager could not deactivate {profileName}.");
            }
        }
        finally
        {
            await _ipv6LeakGuard.RestoreForProfileAsync(profileName, cancellationToken);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private async Task TryDeactivateAndRestoreAfterActivationFailureAsync(string profileName)
    {
        try
        {
            await DeactivateAsync(profileName, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _diagnosticSink($"vpn-networkmanager-cleanup-failed stage=activation-failure profile=\"{RedactDiagnostic(profileName)}\" error=\"{RedactDiagnostic(exception.Message)}\"");
        }
    }

    private async Task BringDownForActivationRetryAsync(string profileName, CancellationToken cancellationToken)
    {
        var down = await _processRunner.RunAsync("nmcli", ["connection", "down", profileName], cancellationToken);
        if (!down.Success
            && !Regex.IsMatch(
                down.StandardError,
                "not\\s+(?:an\\s+)?active|unknown|not found",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            LogCommandFailure("activation-retry-cleanup", profileName, "connection.down", down);
            throw new VpnConfigurationException($"NetworkManager could not reset {profileName} before the guarded IKEv2 retry.");
        }
    }

    private async Task<ActivationFailureDetails?> TryIkeV2GatewayCertificateFallbackAsync(
        VpnProfile profile,
        ActivationFailureDetails initialFailure,
        CancellationToken cancellationToken)
    {
        var candidatePaths = profile.Ikev2GatewayCertificatePaths;
        if (candidatePaths is null || candidatePaths.Count <= 1)
        {
            return initialFailure;
        }

        var candidateLimit = profile.Ikev2AllowPinnedGatewayRootFallback
            ? Math.Min(candidatePaths.Count, 4)
            : candidatePaths.Count;
        var sweepRemainingPinnedRoots = profile.Ikev2AllowPinnedGatewayRootFallback
            && (!initialFailure.JournalAvailable
                || ContainsGatewayTrustFailure(initialFailure.RawDiagnostic)
                || ContainsAuthenticationClassFailure(initialFailure.RawDiagnostic));
        var lastFailure = initialFailure;
        for (var index = 1; index < candidateLimit; index++)
        {
            var certificatePath = candidatePaths[index];
            LogGatewayCertificateAttempt(profile, index + 1, certificatePath, "retry");
            await BringDownForActivationRetryAsync(profile.ProfileName, cancellationToken);

            var vpnData = ReplaceIkeV2CertificatePath(profile.NetworkManagerVpnData, certificatePath);
            var modify = await _processRunner.RunAsync(
                "nmcli",
                ["connection", "modify", profile.ProfileName, "vpn.data", vpnData],
                cancellationToken);
            if (!modify.Success)
            {
                LogCommandFailure("activation-gateway-certificate-fallback", profile.ProfileName, "vpn.data", modify);
                return new ActivationFailureDetails(modify.StandardError);
            }

            var stored = await QueryConnectionSettingAsync(profile.ProfileName, "vpn.data", cancellationToken);
            try
            {
                ValidateIkeV2ProfileWasStored(stored, profile.Ikev2RemoteAddress ?? profile.OuterTransportAddress);
                ValidateStoredIkeV2CertificatePath(stored, certificatePath);
            }
            catch (VpnConfigurationException exception)
            {
                _diagnosticSink($"vpn-networkmanager-verification-failed stage=activation-gateway-certificate-fallback profile=\"{RedactDiagnostic(profile.ProfileName)}\" property=\"vpn.data.certificate\" expected=\"{RedactDiagnostic(certificatePath)}\" actual=\"<redacted>\" error=\"{RedactDiagnostic(exception.Message)}\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\"");
                return new ActivationFailureDetails(exception.Message);
            }

            var up = await _processRunner.RunAsync("nmcli", ["connection", "up", profile.ProfileName], cancellationToken);
            if (up.Success)
            {
                LogGatewayCertificateAttempt(profile, index + 1, certificatePath, "success");
                await RememberSuccessfulIkeV2GatewayRootAsync(profile, certificatePath);
                return null;
            }

            lastFailure = await CaptureActivationFailureAsync(profile.ProfileName, up, cancellationToken);
            LogCommandFailure("activation-gateway-certificate-fallback", profile.ProfileName, "connection.up", up);
            if (sweepRemainingPinnedRoots)
            {
                if (HasExplicitNonCertificateFailure(lastFailure.RawDiagnostic))
                {
                    return lastFailure;
                }

                continue;
            }

            if (!ContainsGatewayTrustFailure(lastFailure.RawDiagnostic))
            {
                return lastFailure;
            }
        }

        if (profile.Ikev2AllowPinnedGatewayRootFallback)
        {
            _diagnosticSink($"vpn-ikev2-gateway-certificate-exhausted profile=\"{RedactDiagnostic(profile.ProfileName)}\" attempts={candidateLimit} limit=4");
        }

        return lastFailure;
    }

    private async Task RememberSuccessfulIkeV2GatewayRootAsync(
        VpnProfile profile,
        string? certificatePath)
    {
        if (_settingsStore is null
            || !profile.Ikev2AllowPinnedGatewayRootFallback
            || string.IsNullOrWhiteSpace(certificatePath))
        {
            return;
        }

        var (subject, fingerprint) = DescribeGatewayCertificate(certificatePath);
        if (!Regex.IsMatch(fingerprint, "^[0-9A-F]{64}$", RegexOptions.CultureInvariant))
        {
            return;
        }

        try
        {
            await _settingsStore.SetAsync(
                IkeV2GatewayTrustPreference.SettingsKey(profile.ProfileName),
                fingerprint,
                CancellationToken.None);
            _diagnosticSink(
                $"vpn-ikev2-gateway-certificate-preference profile=\"{RedactDiagnostic(profile.ProfileName)}\" "
                + $"subject=\"{RedactDiagnostic(subject)}\" fingerprint=\"{RedactDiagnostic(fingerprint)}\" state=remembered");
        }
        catch (Exception exception)
        {
            _diagnosticSink(
                $"vpn-ikev2-gateway-certificate-preference profile=\"{RedactDiagnostic(profile.ProfileName)}\" "
                + $"state=write-failed error=\"{RedactDiagnostic(exception.Message)}\"");
        }
    }

    private async Task<ActivationFailureDetails> CaptureActivationFailureAsync(
        string profileName,
        ProcessResult activationResult,
        CancellationToken cancellationToken)
    {
        string uuid = string.Empty;
        string state = string.Empty;
        string reason = string.Empty;
        string devices = string.Empty;
        ProcessResult? journal = null;

        try
        {
            var uuidResult = await _processRunner.RunAsync(
                "nmcli",
                ["-g", "connection.uuid", "connection", "show", profileName],
                cancellationToken);
            if (uuidResult.Success)
            {
                uuid = NormalizeNmcliValue(uuidResult.StandardOutput);
            }

            var stateResult = await _processRunner.RunAsync(
                "nmcli",
                ["-g", "GENERAL.STATE,GENERAL.REASON,GENERAL.DEVICES", "connection", "show", profileName],
                cancellationToken);
            if (stateResult.Success)
            {
                var stateLines = stateResult.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                state = stateLines.ElementAtOrDefault(0) ?? string.Empty;
                reason = stateLines.ElementAtOrDefault(1) ?? string.Empty;
                devices = stateLines.ElementAtOrDefault(2) ?? string.Empty;
            }

            var journalArguments = new List<string> { "-b", "--no-pager", "--since=-30s" };
            if (IsUuid(uuid))
            {
                journalArguments.Add($"NM_CONNECTION={uuid}");
                var parentDevice = ParseFirstDevice(devices);
                if (!string.IsNullOrWhiteSpace(parentDevice))
                {
                    journalArguments.Add("+");
                    journalArguments.Add($"NM_DEVICE={parentDevice}");
                }
            }
            else
            {
                journalArguments.Add("-u");
                journalArguments.Add("NetworkManager");
            }

            journal = await _processRunner.RunAsync("journalctl", journalArguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _diagnosticSink($"vpn-networkmanager-diagnostics-failed stage=activation profile=\"{RedactDiagnostic(profileName)}\" error=\"{RedactDiagnostic(exception.Message)}\"");
        }

        var rawDiagnostic = string.Join(
            " ",
            activationResult.StandardError,
            state,
            reason,
            devices,
            journal?.StandardOutput,
            journal?.StandardError);
        var journalAvailable = journal is { Success: true }
            && !string.IsNullOrWhiteSpace(journal.StandardOutput);
        var redactedJournal = journal is null
            ? string.Empty
            : RedactDiagnostic(journal.StandardOutput + " " + journal.StandardError);
        _diagnosticSink($"vpn-networkmanager-activation-diagnostics profile=\"{RedactDiagnostic(profileName)}\" uuid=\"{RedactDiagnostic(uuid)}\" state=\"{RedactDiagnostic(state)}\" reason=\"{RedactDiagnostic(reason)}\" devices=\"{RedactDiagnostic(devices)}\" journal_available={(journalAvailable ? "true" : "false")} journal=\"{redactedJournal}\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\"");

        return new ActivationFailureDetails(rawDiagnostic, journalAvailable);
    }

    private void LogGatewayCertificateAttempt(
        VpnProfile profile,
        int attempt,
        string certificatePath,
        string stage)
    {
        var (subject, fingerprint) = DescribeGatewayCertificate(certificatePath);
        var eventName = stage switch
        {
            "initial" => "vpn-ikev2-gateway-certificate-initial",
            "success" => "vpn-ikev2-gateway-certificate-succeeded",
            _ => "vpn-ikev2-gateway-certificate-attempt"
        };
        _diagnosticSink(
            $"{eventName} profile=\"{RedactDiagnostic(profile.ProfileName)}\" attempt={attempt} "
            + $"path=\"{RedactDiagnostic(certificatePath)}\" subject=\"{RedactDiagnostic(subject)}\" "
            + $"fingerprint=\"{RedactDiagnostic(fingerprint)}\"");
    }

    private static (string Subject, string Fingerprint) DescribeGatewayCertificate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ("unknown", "unknown");
            }

            using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(path));
            return (
                certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (IOException)
        {
            return ("unknown", "unknown");
        }
        catch (UnauthorizedAccessException)
        {
            return ("unknown", "unknown");
        }
        catch (CryptographicException)
        {
            return ("unknown", "unknown");
        }
        catch (ArgumentException)
        {
            return ("unknown", "unknown");
        }
    }

    private static VpnConfigurationException CreateActivationFailure(string profileName, ActivationFailureDetails failure)
    {
        var detail = Regex.Replace(failure.RawDiagnostic, @"[\r\n\t ]+", " ", RegexOptions.CultureInvariant).Trim();
        if (detail.Length > 240)
        {
            detail = detail[..240] + "…";
        }

        return new VpnConfigurationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"NetworkManager could not activate {profileName}."
                : $"NetworkManager could not activate {profileName}. NetworkManager reported: {RedactDiagnostic(detail)}");
    }

    private static bool ShouldRetryIkeV2WithGatewayCertificate(
        VpnProfile profile,
        ActivationFailureDetails failure)
    {
        if (!profile.Ikev2AllowPinnedGatewayRootFallback)
        {
            return false;
        }

        if (ContainsGatewayTrustFailure(failure.RawDiagnostic))
        {
            return true;
        }

        if (!profile.Ikev2AllowPinnedGatewayRootFallback || failure.JournalAvailable)
        {
            return profile.Ikev2AllowPinnedGatewayRootFallback
                && !HasExplicitNonCertificateFailure(failure.RawDiagnostic)
                && ContainsAuthenticationClassFailure(failure.RawDiagnostic);
        }

        return !HasExplicitNonCertificateFailure(failure.RawDiagnostic);
    }

    private static bool ContainsGatewayTrustFailure(string text)
        => Regex.IsMatch(
            text,
            "no issuer certificate found|no trusted (?:[A-Za-z0-9]+ )*public key found|issuer certificate.*not found",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasExplicitNonCertificateFailure(string text)
        => Regex.IsMatch(
            text,
            "transport|proposal|no proposal|plugin|cancel(?:led|lation)?|private[- ]?key|userkey|password|passphrase|local identity|identity.*(?:failed|invalid)|client certificate|user certificate|certificate.*(?:local|client)|authentication of .*myself|local authentication|credential|host unreachable|connection refused|timed? out|timeout|endpoint|unreachable|no route|no response|network is unreachable",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool ContainsAuthenticationClassFailure(string text)
        => Regex.IsMatch(
            text,
            "connect-failed|login-failed|authentication|auth[_ -]?failed|vpn service failed|activation failed|unknown reason",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record ActivationFailureDetails(string RawDiagnostic, bool JournalAvailable = true);

    private async Task TryDeleteProvisionedProfilesAsync(IEnumerable<string> profileNames)
    {
        foreach (var profileName in profileNames
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                var delete = await _processRunner.RunAsync("nmcli", ["connection", "delete", profileName], CancellationToken.None);
                if (!delete.Success && !Regex.IsMatch(delete.StandardError, "unknown|not found", RegexOptions.IgnoreCase))
                {
                    LogCommandFailure("provisioning-cleanup", profileName, "connection.delete", delete);
                }
            }
            catch (Exception exception)
            {
                _diagnosticSink($"vpn-networkmanager-cleanup-failed stage=provisioning profile=\"{RedactDiagnostic(profileName)}\" error=\"{RedactDiagnostic(exception.Message)}\"");
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetActiveLibreGuardProfilesAsync(CancellationToken cancellationToken)
        => await GetLibreGuardProfilesAsync(activeOnly: true, cancellationToken);

    public async Task<IReadOnlyList<string>> GetLibreGuardProfilesAsync(CancellationToken cancellationToken)
        => await GetLibreGuardProfilesAsync(activeOnly: false, cancellationToken);

    public async Task DisconnectLibreGuardProfilesAsync(CancellationToken cancellationToken)
    {
        foreach (var profileName in await GetActiveLibreGuardProfilesAsync(cancellationToken))
        {
            await DeactivateAsync(profileName, cancellationToken);
        }
    }

    public async Task DeleteLibreGuardProfilesAsync(string? excludeProfileName, CancellationToken cancellationToken)
    {
        foreach (var profileName in await GetLibreGuardProfilesAsync(cancellationToken))
        {
            if (ShouldSkipProfile(profileName, excludeProfileName))
            {
                continue;
            }

            await DeleteLibreGuardProfileAsync(profileName, cancellationToken);
        }
    }

    public async Task DeleteLibreGuardProfileAsync(string profileName, CancellationToken cancellationToken)
    {
        if (!IsLibreGuardProfileName(profileName))
        {
            throw new VpnConfigurationException("LibreGuard refused to delete a profile it does not own.");
        }

        var delete = await _processRunner.RunAsync("nmcli", ["connection", "delete", profileName], cancellationToken);
        if (!delete.Success && !Regex.IsMatch(delete.StandardError, "unknown|not found", RegexOptions.IgnoreCase))
        {
            throw new VpnConfigurationException($"NetworkManager could not delete {profileName}.");
        }
    }

    public Task CleanupLibreGuardArtifactsAsync(string? excludeProfileName, CancellationToken cancellationToken)
    {
        foreach (var directory in new[]
                 {
                     XdgPaths.VpnCredentialDirectory,
                     XdgPaths.LegacyVpnConfigDirectory,
                     XdgPaths.NewerVpnCredentialDirectory
                 }.Distinct(StringComparer.Ordinal))
        {
            CleanupLibreGuardArtifacts(directory, excludeProfileName, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task CleanupLibreGuardProfileArtifactsAsync(string profileName, CancellationToken cancellationToken)
    {
        foreach (var directory in new[]
                 {
                     XdgPaths.VpnCredentialDirectory,
                     XdgPaths.LegacyVpnConfigDirectory,
                     XdgPaths.NewerVpnCredentialDirectory
                 }.Distinct(StringComparer.Ordinal))
        {
            CleanupLibreGuardArtifacts(directory, profileName, cancellationToken, includeOnlyProfile: true);
        }

        return Task.CompletedTask;
    }

    private static void CleanupLibreGuardArtifacts(
        string directory,
        string? excludeProfileName,
        CancellationToken cancellationToken,
        bool includeOnlyProfile = false)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!IsLibreGuardProfileName(fileName))
            {
                continue;
            }

            var belongsToSelectedProfile = !string.IsNullOrWhiteSpace(excludeProfileName)
                && fileName.StartsWith(excludeProfileName, StringComparison.OrdinalIgnoreCase);
            if (includeOnlyProfile && !belongsToSelectedProfile)
            {
                continue;
            }

            if (!includeOnlyProfile && belongsToSelectedProfile)
            {
                continue;
            }

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
    }

    public async Task<string?> GetActiveDeviceNameAsync(string profileName, CancellationToken cancellationToken)
    {
        var show = await _processRunner.RunAsync("nmcli", [
            "-g",
            "GENERAL.DEVICES",
            "connection",
            "show",
            profileName
        ], cancellationToken);

        if (!show.Success)
        {
            return null;
        }

        var parentDevices = show.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(device => !string.Equals(device, "--", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

        var configuredInterface = await TryQueryConnectionSettingAsync(
            profileName,
            "connection.interface-name",
            cancellationToken);
        var deviceStates = await QueryActiveDeviceStatesAsync(cancellationToken);
        var associatedTunnel = deviceStates
            .Where(device => device.IsActive && IsTunnelDevice(device))
            .Where(device => IsAssociatedWithProfile(device, profileName, configuredInterface))
            .OrderByDescending(device => string.Equals(device.Connection, profileName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(device => string.Equals(device.Name, configuredInterface, StringComparison.Ordinal))
            .ThenByDescending(device => device.Name.StartsWith("lgvpn", StringComparison.OrdinalIgnoreCase))
            .Select(device => device.Name)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(associatedTunnel))
        {
            return associatedTunnel;
        }

        // NetworkManager 1.46 can expose an OpenVPN TUN device as a separately assumed,
        // externally managed connection whose connection name is just the interface name
        // (for example, tun0). In that state neither GENERAL.DEVICES nor device status ties
        // the interface back to the VPN profile. Preserve ead2854's fail-closed fallback:
        // trust the route device only when its source address belongs to the active profile.
        var routedTunnel = await TryResolveActiveProfileRouteDeviceAsync(
            profileName,
            deviceStates,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(routedTunnel))
        {
            _diagnosticSink($"vpn-device-discovery-fallback profile=\"{RedactDiagnostic(profileName)}\" device=\"{RedactDiagnostic(routedTunnel)}\" proof=active-profile-address-and-default-route");
            return routedTunnel;
        }

        // Older NetworkManager versions can expose the configured tunnel interface through
        // GENERAL.DEVICES without returning a useful device-status table. Never use a
        // physical parent as a VPN device; only accept a name that is unambiguously virtual.
        return parentDevices
            .Concat(string.IsNullOrWhiteSpace(configuredInterface) ? [] : [configuredInterface])
            .Where(IsLikelyTunnelDeviceName)
            .FirstOrDefault();
    }

    private async Task<string?> TryResolveActiveProfileRouteDeviceAsync(
        string profileName,
        IReadOnlyList<NetworkManagerDeviceState> deviceStates,
        CancellationToken cancellationToken)
    {
        var addresses = await _processRunner.RunAsync(
            "nmcli",
            ["-g", "IP4.ADDRESS", "connection", "show", "--active", "id", profileName],
            cancellationToken);
        if (!addresses.Success)
        {
            return null;
        }

        var profileAddresses = ExtractIpv4InterfaceAddresses(addresses.StandardOutput);
        if (profileAddresses.Count == 0)
        {
            return null;
        }

        var route = await _processRunner.RunAsync(
            "ip",
            ["-4", "route", "get", "1.1.1.1"],
            cancellationToken);
        if (!route.Success
            || !TryParseRouteDeviceAndSource(route.StandardOutput, out var deviceName, out var sourceAddress)
            || !profileAddresses.Contains(sourceAddress))
        {
            return null;
        }

        var isKnownTunnel = deviceStates.Any(device =>
            string.Equals(device.Name, deviceName, StringComparison.Ordinal)
            && IsTunnelDevice(device));
        return isKnownTunnel || IsLikelyTunnelDeviceName(deviceName)
            ? deviceName
            : null;
    }

    private static IReadOnlySet<string> ExtractIpv4InterfaceAddresses(string value)
        => value
            .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Split('/', 2)[0].Trim('(', ')', '[', ']', '{', '}', '"'))
            .Where(token => IPAddress.TryParse(token, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool TryParseRouteDeviceAndSource(
        string routeOutput,
        out string deviceName,
        out string sourceAddress)
    {
        deviceName = string.Empty;
        sourceAddress = string.Empty;
        var tokens = routeOutput.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (string.Equals(tokens[index], "dev", StringComparison.Ordinal))
            {
                deviceName = tokens[index + 1];
            }
            else if (string.Equals(tokens[index], "src", StringComparison.Ordinal))
            {
                sourceAddress = tokens[index + 1];
            }
        }

        return !string.IsNullOrWhiteSpace(deviceName)
            && IPAddress.TryParse(sourceAddress, out var source)
            && source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    private async Task<IReadOnlyList<NetworkManagerDeviceState>> QueryActiveDeviceStatesAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "nmcli",
            ["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"],
            cancellationToken);
        if (!result.Success)
        {
            return [];
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseDeviceState)
            .Where(state => state is not null)
            .Select(state => state!)
            .ToArray();
    }

    private async Task<string> TryQueryConnectionSettingAsync(
        string profileName,
        string propertyName,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "nmcli",
            ["-g", propertyName, "connection", "show", profileName],
            cancellationToken);
        return result.Success ? NormalizeNmcliValue(result.StandardOutput) : string.Empty;
    }

    private static NetworkManagerDeviceState? ParseDeviceState(string line)
    {
        var fields = line.Split(':');
        if (fields.Length < 4 || string.IsNullOrWhiteSpace(fields[0]))
        {
            return null;
        }

        return new NetworkManagerDeviceState(
            fields[0].Trim(),
            fields[1].Trim(),
            fields[2].Trim(),
            string.Join(':', fields.Skip(3)).Trim());
    }

    private static bool IsAssociatedWithProfile(
        NetworkManagerDeviceState device,
        string profileName,
        string configuredInterface)
        => string.Equals(device.Connection, profileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.Name, configuredInterface, StringComparison.Ordinal)
            || (string.IsNullOrWhiteSpace(device.Connection)
                && string.Equals(device.Name, configuredInterface, StringComparison.Ordinal));

    private static bool IsTunnelDevice(NetworkManagerDeviceState device)
        => IsTunnelType(device.Type) || IsLikelyTunnelDeviceName(device.Name);

    private static bool IsTunnelType(string type)
        => type.Equals("tun", StringComparison.OrdinalIgnoreCase)
            || type.Equals("xfrm", StringComparison.OrdinalIgnoreCase)
            || type.Equals("vpn", StringComparison.OrdinalIgnoreCase)
            || type.Equals("ip-tunnel", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyTunnelDeviceName(string deviceName)
        => deviceName.StartsWith("lgvpn", StringComparison.OrdinalIgnoreCase)
            || deviceName.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
            || deviceName.StartsWith("xfrm", StringComparison.OrdinalIgnoreCase);

    private static string GetCurrentUserConnectionPermission()
    {
        var userName = Environment.UserName.Trim();
        if (string.IsNullOrWhiteSpace(userName) || userName.Contains(':'))
        {
            throw new VpnConfigurationException("LibreGuard could not determine the active user required to access IKEv2 credentials.");
        }

        return $"user:{userName}";
    }

    private bool RequiresFedoraNetworkManager156CredentialWorkaround()
    {
        if (_networkManagerVersionValue is not { Major: 1, Minor: 56 })
        {
            return false;
        }

        try
        {
            if (!_fileExists(OsReleasePath))
            {
                return false;
            }

            return _readAllText(OsReleasePath)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line =>
                {
                    var separator = line.IndexOf('=');
                    if (separator < 0 || !line[..separator].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return line[(separator + 1)..]
                        .Trim()
                        .Trim('"', '\'')
                        .Equals("fedora", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch (Exception exception)
        {
            _diagnosticSink($"vpn-ikev2-fedora-credential-helper-workaround state=os-detection-failed error=\"{RedactDiagnostic(exception.Message)}\"");
            return false;
        }
    }

    private async Task<IReadOnlyList<PosixAclSnapshot>> GrantFedoraIkeV2CredentialAccessAsync(
        VpnProfile profile,
        CancellationToken cancellationToken)
    {
        var targets = BuildFedoraIkeV2AclTargets(profile);
        var snapshots = new List<PosixAclSnapshot>(targets.Count);

        try
        {
            foreach (var target in targets)
            {
                var snapshot = await CapturePosixAclAsync(target, cancellationToken);
                snapshots.Add(snapshot);

                var grant = await _processRunner.RunAsync(
                    "setfacl",
                    ["--modify", target.IsDirectory ? "user:0:--x" : "user:0:r--", target.Path],
                    cancellationToken);
                if (!grant.Success)
                {
                    LogCommandFailure(
                        "fedora-credential-acl-enable",
                        profile.ProfileName,
                        target.IsDirectory ? "directory-acl" : "file-acl",
                        grant);
                    throw new VpnConfigurationException(
                        "LibreGuard could not grant Fedora strongSwan temporary access to its private IKEv2 credentials. Reinstall the RPM so the acl dependency is present.");
                }
            }
        }
        catch (Exception grantException)
        {
            try
            {
                await RestorePosixAclsAsync(profile.ProfileName, snapshots, CancellationToken.None);
            }
            catch (Exception restoreException)
            {
                throw new AggregateException(
                    "LibreGuard could neither enable nor roll back the Fedora IKEv2 credential ACL.",
                    grantException,
                    restoreException);
            }

            throw;
        }

        _diagnosticSink($"vpn-ikev2-fedora-credential-acl profile=\"{RedactDiagnostic(profile.ProfileName)}\" target_count={snapshots.Count} state=granted");
        return snapshots;
    }

    private IReadOnlyList<PosixAclTarget> BuildFedoraIkeV2AclTargets(VpnProfile profile)
    {
        if (profile.Ikev2CredentialPaths is not { Count: > 0 })
        {
            throw new VpnConfigurationException(
                "LibreGuard could not identify the IKEv2 credential files required by Fedora strongSwan.");
        }

        if (string.IsNullOrWhiteSpace(_userHomeDirectory))
        {
            throw new VpnConfigurationException(
                "LibreGuard could not determine the current user's home directory for Fedora IKEv2 credential access.");
        }

        var homeDirectory = Path.GetFullPath(_userHomeDirectory);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var directoryPaths = new HashSet<string>(pathComparer);
        var filePaths = new HashSet<string>(pathComparer);

        foreach (var credentialPath in profile.Ikev2CredentialPaths)
        {
            if (string.IsNullOrWhiteSpace(credentialPath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(credentialPath);
            if (!IsPathWithinDirectory(fullPath, homeDirectory))
            {
                throw new VpnConfigurationException(
                    "LibreGuard refused to grant Fedora strongSwan access to an IKEv2 credential outside the current user's home directory.");
            }

            FileSecurity.EnsureNotSymbolicLink(fullPath);
            filePaths.Add(fullPath);

            var currentDirectory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                if (!IsPathWithinDirectory(currentDirectory, homeDirectory))
                {
                    throw new VpnConfigurationException(
                        "LibreGuard refused to modify an IKEv2 credential directory outside the current user's home directory.");
                }

                FileSecurity.EnsureNotSymbolicLink(currentDirectory);
                directoryPaths.Add(currentDirectory);
                if (pathComparer.Equals(currentDirectory, homeDirectory))
                {
                    break;
                }

                currentDirectory = Path.GetDirectoryName(currentDirectory);
            }
        }

        if (filePaths.Count == 0 || !directoryPaths.Contains(homeDirectory))
        {
            throw new VpnConfigurationException(
                "LibreGuard could not build the Fedora IKEv2 credential access scope.");
        }

        return directoryPaths
            .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
            .Select(path => new PosixAclTarget(path, IsDirectory: true))
            .Concat(filePaths.OrderBy(path => path, pathComparer)
                .Select(path => new PosixAclTarget(path, IsDirectory: false)))
            .ToArray();
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private async Task<PosixAclSnapshot> CapturePosixAclAsync(
        PosixAclTarget target,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "getfacl",
            ["--access", "--numeric", "--omit-header", "--absolute-names", target.Path],
            cancellationToken);
        if (!result.Success)
        {
            LogCommandFailure(
                "fedora-credential-acl-snapshot",
                null,
                target.IsDirectory ? "directory-acl" : "file-acl",
                result);
            throw new VpnConfigurationException(
                "LibreGuard could not snapshot the Fedora IKEv2 credential permissions. Reinstall the RPM so the acl dependency is present.");
        }

        var entries = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var commentIndex = line.IndexOf('#');
                return (commentIndex >= 0 ? line[..commentIndex] : line).Trim();
            })
            .Where(line => line.StartsWith("user:", StringComparison.Ordinal)
                || line.StartsWith("group:", StringComparison.Ordinal)
                || line.StartsWith("mask:", StringComparison.Ordinal)
                || line.StartsWith("other:", StringComparison.Ordinal))
            .ToArray();

        if (!entries.Any(entry => entry.StartsWith("user::", StringComparison.Ordinal))
            || !entries.Any(entry => entry.StartsWith("group::", StringComparison.Ordinal))
            || !entries.Any(entry => entry.StartsWith("other::", StringComparison.Ordinal)))
        {
            throw new VpnConfigurationException(
                "LibreGuard received an invalid Fedora IKEv2 credential ACL snapshot and refused to change it.");
        }

        return new PosixAclSnapshot(target.Path, string.Join(',', entries));
    }

    private async Task RestorePosixAclsAsync(
        string profileName,
        IReadOnlyList<PosixAclSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var snapshot in snapshots.Reverse())
        {
            var restore = await _processRunner.RunAsync(
                "setfacl",
                ["--set", snapshot.AccessAcl, snapshot.Path],
                cancellationToken);
            if (!restore.Success)
            {
                failed = true;
                LogCommandFailure(
                    "fedora-credential-acl-restore",
                    profileName,
                    "credential-acl",
                    restore);
            }
        }

        if (failed)
        {
            throw new VpnConfigurationException(
                "LibreGuard could not restore one or more private Fedora IKEv2 credential ACLs.");
        }

        _diagnosticSink($"vpn-ikev2-fedora-credential-acl profile=\"{RedactDiagnostic(profileName)}\" target_count={snapshots.Count} state=restored");
    }

    private async Task RestoreFedoraIkeV2CredentialWorkaroundAsync(
        string profileName,
        IReadOnlyList<PosixAclSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        Exception? restoreFailure = null;
        try
        {
            await SetIkeV2ConnectionPermissionAsync(
                profileName,
                GetCurrentUserConnectionPermission(),
                "fedora-credential-helper-workaround-restore",
                cancellationToken);
        }
        catch (Exception exception)
        {
            restoreFailure = exception;
        }

        try
        {
            await RestorePosixAclsAsync(profileName, snapshots, cancellationToken);
        }
        catch (Exception exception)
        {
            restoreFailure ??= exception;
        }

        if (restoreFailure is not null)
        {
            throw restoreFailure;
        }
    }

    private async Task SetIkeV2ConnectionPermissionAsync(
        string profileName,
        string permission,
        string stage,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            "nmcli",
            ["connection", "modify", profileName, "connection.permissions", permission],
            cancellationToken);
        if (!result.Success)
        {
            LogCommandFailure(stage, profileName, "connection.permissions", result);
            throw new VpnConfigurationException(
                string.IsNullOrWhiteSpace(permission)
                    ? "LibreGuard could not apply the Fedora NetworkManager 1.56 IKEv2 credential-helper workaround."
                    : "LibreGuard could not restore the IKEv2 profile's private user permission after activation.");
        }
    }

    private sealed record NetworkManagerDeviceState(string Name, string Type, string State, string Connection)
    {
        public bool IsActive
            => State.StartsWith("connected", StringComparison.OrdinalIgnoreCase)
                || State.StartsWith("activated", StringComparison.OrdinalIgnoreCase)
                || State.StartsWith("connecting", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ConfigureProfileAsync(
        string currentName,
        string desiredName,
        string? vpnData,
        IReadOnlyList<(string Name, string Value)> fullTunnelSettings,
        CancellationToken cancellationToken,
        string? connectionPermission = null)
    {
        var modifyArguments = new List<string>
        {
            "connection",
            "modify",
            currentName,
            "connection.id",
            desiredName,
            "connection.autoconnect",
            "no"
        };

        if (!string.IsNullOrWhiteSpace(connectionPermission))
        {
            modifyArguments.Add("connection.permissions");
            modifyArguments.Add(connectionPermission);
        }

        if (!string.IsNullOrWhiteSpace(vpnData))
        {
            modifyArguments.Add("vpn.data");
            modifyArguments.Add(string.Empty);
        }

        AppendSettings(modifyArguments, fullTunnelSettings);
        AppendSettings(modifyArguments, GetEffectivePrivateDnsSettings());

        var modify = await _processRunner.RunAsync("nmcli", modifyArguments, cancellationToken);
        if (!modify.Success)
        {
            LogCommandFailure(
                "profile-configuration",
                desiredName,
                _supportsRoutedDns ? "ipv4.routed-dns" : "ipv4.routes",
                modify);
            throw new VpnConfigurationException($"Failed to configure NetworkManager profile {desiredName} (exit code {modify.ExitCode}).");
        }

        if (!string.IsNullOrWhiteSpace(vpnData))
        {
            foreach (var vpnDataItem in SplitVpnDataItems(vpnData))
            {
                var append = await _processRunner.RunAsync(
                    "nmcli",
                    ["connection", "modify", desiredName, "+vpn.data", vpnDataItem],
                    cancellationToken);
                if (!append.Success)
                {
                    LogCommandFailure("ikev2-profile-vpn-data", desiredName, GetVpnDataKey(vpnDataItem), append);
                    throw new VpnConfigurationException($"Failed to add IKEv2 NetworkManager setting '{GetVpnDataKey(vpnDataItem)}'.");
                }
            }
        }
    }

    private async Task VerifyConfiguredProfileAsync(
        string profileName,
        string? expectedVpnData,
        string expectedRemoteAddress,
        IReadOnlyList<(string Name, string Value)> fullTunnelSettings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(expectedVpnData))
        {
            var vpnData = await QueryConnectionSettingAsync(profileName, "vpn.data", cancellationToken);
            try
            {
                ValidateIkeV2ProfileWasStored(vpnData, expectedRemoteAddress);
            }
            catch (VpnConfigurationException)
            {
                _diagnosticSink($"vpn-networkmanager-verification-failed stage=profile-verification profile=\"{RedactDiagnostic(profileName)}\" property=\"vpn.data\" expected=\"address={RedactDiagnostic(expectedRemoteAddress)};method=key;remote-ts=0.0.0.0/0\" actual=\"vpn.data=<redacted>\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\"");
                throw;
            }
        }

        foreach (var (name, expectedValue) in fullTunnelSettings.Concat(GetEffectivePrivateDnsSettings()))
        {
            var value = await QueryConnectionSettingAsync(profileName, name, cancellationToken);
            if (!ValuesMatch(name, expectedValue, value))
            {
                _diagnosticSink($"vpn-networkmanager-verification-failed stage=profile-verification profile=\"{RedactDiagnostic(profileName)}\" property=\"{name}\" expected=\"{RedactDiagnostic(expectedValue)}\" actual=\"{RedactDiagnostic(value)}\" normalized_expected=\"{RedactDiagnostic(NormalizeSettingForDiagnostic(name, expectedValue))}\" normalized_actual=\"{RedactDiagnostic(NormalizeSettingForDiagnostic(name, value))}\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\"");
                throw new VpnConfigurationException($"NetworkManager did not store the required setting '{name}' for profile {profileName}.");
            }
        }

    }

    private async Task<string> QueryConnectionSettingAsync(
        string profileName,
        string propertyName,
        CancellationToken cancellationToken)
    {
        var show = await _processRunner.RunAsync("nmcli", [
                "-g",
                propertyName,
                "connection",
                "show",
                profileName
            ], cancellationToken);

        if (!show.Success)
        {
            LogCommandFailure("profile-verification", profileName, propertyName, show);
            throw new VpnConfigurationException($"Failed to verify NetworkManager setting '{propertyName}' for profile {profileName} (exit code {show.ExitCode}).");
        }

        return NormalizeNmcliValue(show.StandardOutput);
    }

    private IEnumerable<(string Name, string Value)> GetEffectivePrivateDnsSettings()
    {
        foreach (var setting in PrivateDnsSettings)
        {
            yield return setting;
        }

        yield return _supportsRoutedDns
            ? ("ipv4.routed-dns", "yes")
            : ("ipv4.routes", $"{PrivateDnsAddress}/32");
    }

    private static void AppendSettings(
        ICollection<string> arguments,
        IEnumerable<(string Name, string Value)> settings)
    {
        foreach (var (name, value) in settings)
        {
            arguments.Add(name);
            arguments.Add(value);
        }
    }

    private static string NormalizeNmcliValue(string value)
    {
        var normalized = value.Trim();
        return string.Equals(normalized, "--", StringComparison.Ordinal) ? string.Empty : normalized;
    }

    private static bool ValuesMatch(string propertyName, string expected, string actual)
    {
        var normalizedExpected = NormalizeNmcliValue(expected);
        var normalizedActual = NormalizeNmcliValue(actual);

        if (propertyName is "ipv4.never-default" or "ipv4.ignore-auto-routes" or "ipv6.never-default" or "ipv6.ignore-auto-routes"
            or "ipv4.ignore-auto-dns" or "ipv6.ignore-auto-dns" or "ipv4.routed-dns")
        {
            return TryNormalizeBoolean(normalizedExpected, out var expectedBoolean)
                && TryNormalizeBoolean(normalizedActual, out var actualBoolean)
                && expectedBoolean == actualBoolean;
        }

        if (propertyName is "ipv4.dns-priority" or "ipv6.dns-priority")
        {
            return int.TryParse(normalizedExpected, out var expectedPriority)
                && int.TryParse(normalizedActual, out var actualPriority)
                && expectedPriority == actualPriority;
        }

        if (propertyName is "ipv4.dns" or "ipv6.dns")
        {
            return ParseIpAddressList(normalizedExpected).SetEquals(ParseIpAddressList(normalizedActual));
        }

        if (propertyName is "ipv4.dns-search" or "ipv6.dns-search")
        {
            return ParseTokenSet(normalizedExpected).SetEquals(ParseTokenSet(normalizedActual));
        }

        if (propertyName == "ipv4.routes")
        {
            return ContainsIpv4HostRoute(normalizedActual, PrivateDnsAddress)
                && ContainsIpv4HostRoute(normalizedExpected, PrivateDnsAddress);
        }

        return string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSettingForDiagnostic(string propertyName, string value)
    {
        var normalized = NormalizeNmcliValue(value);
        if (propertyName is "ipv4.dns" or "ipv6.dns")
        {
            return string.Join(',', ParseIpAddressList(normalized).OrderBy(address => address, StringComparer.OrdinalIgnoreCase));
        }

        if (propertyName is "ipv4.dns-search" or "ipv6.dns-search")
        {
            return string.Join(',', ParseTokenSet(normalized).OrderBy(token => token, StringComparer.OrdinalIgnoreCase));
        }

        if (propertyName == "ipv4.routes")
        {
            return ContainsIpv4HostRoute(normalized, PrivateDnsAddress)
                ? $"contains:{PrivateDnsAddress}/32"
                : normalized;
        }

        if (TryNormalizeBoolean(normalized, out var booleanValue))
        {
            return booleanValue ? "yes" : "no";
        }

        return normalized;
    }

    private static bool TryNormalizeBoolean(string value, out bool normalized)
    {
        switch (NormalizeNmcliValue(value).ToLowerInvariant())
        {
            case "yes":
            case "true":
            case "1":
                normalized = true;
                return true;
            case "no":
            case "false":
            case "0":
                normalized = false;
                return true;
            default:
                normalized = false;
                return false;
        }
    }

    private static HashSet<string> ParseIpAddressList(string value)
        => ExtractIpAddresses(value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ParseTokenSet(string value)
        => value
            .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsIpv4HostRoute(string value, string address)
        => Regex.IsMatch(
            value,
            $@"(?<![\d.]){Regex.Escape(address)}\s*/\s*32(?!\d)",
            RegexOptions.CultureInvariant);

    private static bool TryParseNetworkManagerVersion(string output, out Version version)
    {
        var match = Regex.Match(output, @"(?<!\d)(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            version = new Version(0, 0);
            return false;
        }

        version = new Version(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0);
        return true;
    }

    private void LogCommandFailure(
        string stage,
        string? profileName,
        string property,
        ProcessResult result)
    {
        var profile = string.IsNullOrWhiteSpace(profileName) ? string.Empty : $" profile=\"{RedactDiagnostic(profileName)}\"";
        _diagnosticSink($"vpn-networkmanager-command-failed stage=\"{stage}\"{profile} property=\"{RedactDiagnostic(property)}\" exit_code={result.ExitCode} stderr=\"{RedactDiagnostic(result.StandardError)}\" network_manager_version=\"{RedactDiagnostic(_networkManagerVersion)}\"");
    }

    private static string RedactDiagnostic(string value)
    {
        var redacted = Regex.Replace(value ?? string.Empty, @"(?i)(password|passphrase|private[-_ ]?key|userkey|pkcs12|secret|token)\s*[=:]\s*\S+", "$1=<redacted>", RegexOptions.CultureInvariant);
        redacted = Regex.Replace(redacted, @"(?im)vpn\.data\s*[=:][^\r\n]*", "vpn.data=<redacted>", RegexOptions.CultureInvariant);
        redacted = Regex.Replace(redacted, @"[\r\n\t ]+", " ", RegexOptions.CultureInvariant).Trim();
        return redacted.Length <= 512 ? redacted : redacted[..512] + "…";
    }

    private async Task VerifyFullTunnelRoutingAsync(string profileName, CancellationToken cancellationToken)
    {
        var parentOrTunnelDevice = await GetActiveDeviceNameAsync(profileName, cancellationToken);
        if (string.IsNullOrWhiteSpace(parentOrTunnelDevice))
        {
            throw new VpnConfigurationException($"NetworkManager did not expose an active VPN device for {profileName}; refusing to enable traffic.");
        }

        await VerifyActivePrivateDnsAsync(profileName, cancellationToken);
        var deviceName = await VerifyResolvectlPrivateDnsAsync(profileName, parentOrTunnelDevice, cancellationToken);
        VerifyBrowserDohProtection(profileName);

        // The VPN server's outer transport route is intentionally not checked here: it must remain
        // on the physical interface so the tunnel can be established. All in-tunnel destinations
        // and the private resolver must use the active VPN device.
        foreach (var (target, description) in new[]
                 {
                     (PrivateDnsAddress, "private DNS resolver"),
                     ("1.1.1.1", "IPv4 full-tunnel traffic")
                 })
        {
            var route = await _processRunner.RunAsync("ip", ["-4", "route", "get", target], cancellationToken);
            if (!route.Success)
            {
                LogCommandFailure("route-verification", profileName, $"-4 route get {target}", route);
                throw new VpnConfigurationException($"NetworkManager could not verify the {description} route for {profileName}; refusing to enable traffic.");
            }

            if (!RouteUsesDevice(route.StandardOutput, deviceName))
            {
                _diagnosticSink($"vpn-route-verification-failed profile=\"{RedactDiagnostic(profileName)}\" target=\"{target}\" expected_device=\"{RedactDiagnostic(deviceName)}\" route=\"{RedactDiagnostic(route.StandardOutput)}\"");
                throw new VpnConfigurationException($"The {description} route for {profileName} does not use VPN device {deviceName}; refusing to enable traffic.");
            }
        }

        await _ipv6LeakGuard.VerifyAfterActivationAsync(profileName, deviceName, cancellationToken);
    }

    private sealed record PosixAclTarget(string Path, bool IsDirectory);

    private sealed record PosixAclSnapshot(string Path, string AccessAcl);

    private void VerifyBrowserDohProtection(string profileName)
    {
        if (!_verifyBrowserDohProtection)
        {
            return;
        }

        var canaryPresent = false;
        if (_fileExists(SystemHostsPath))
        {
            try
            {
                canaryPresent = _readAllText(SystemHostsPath)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(ManagedDohCanaryLine, StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _diagnosticSink($"vpn-browser-doh-verification-failed profile=\"{RedactDiagnostic(profileName)}\" reason=hosts-unreadable error=\"{RedactDiagnostic(exception.Message)}\"");
            }
        }

        if (!canaryPresent)
        {
            _diagnosticSink($"vpn-browser-doh-verification-failed profile=\"{RedactDiagnostic(profileName)}\" reason=canary-missing");
            throw new VpnConfigurationException(
                "LibreGuard could not verify the browser DNS-over-HTTPS canary signal. Automatic browser secure DNS could bypass the private resolver; refusing to enable traffic.");
        }

        _diagnosticSink($"vpn-browser-doh-verification profile=\"{RedactDiagnostic(profileName)}\" status=active scope=automatic-doh");
    }

    private async Task VerifyActivePrivateDnsAsync(string profileName, CancellationToken cancellationToken)
    {
        var dns = await _processRunner.RunAsync("nmcli", [
            "-g",
            "IP4.DNS,IP6.DNS",
            "connection",
            "show",
            "--active",
            "id",
            profileName
        ], cancellationToken);
        if (!dns.Success)
        {
            LogCommandFailure("active-dns-verification", profileName, "IP4.DNS,IP6.DNS", dns);
            throw new VpnConfigurationException($"NetworkManager could not verify active DNS for {profileName}; refusing to enable traffic.");
        }

        var configuredResolvers = ExtractIpAddresses(dns.StandardOutput);
        if (HasOnlyPrivateResolver(configuredResolvers))
        {
            return;
        }

        _diagnosticSink($"vpn-active-dns-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_resolver=\"{PrivateDnsAddress}\" actual_resolvers=\"{RedactDiagnostic(dns.StandardOutput)}\"");
        throw new VpnConfigurationException($"The active DNS configuration for {profileName} is not limited to private resolver {PrivateDnsAddress}; refusing to enable traffic.");
    }

    private async Task<string> VerifyResolvectlPrivateDnsAsync(
        string profileName,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var status = await _processRunner.RunAsync("resolvectl", ["status"], cancellationToken);
        if (IsResolvectlUnavailable(status))
        {
            _diagnosticSink($"vpn-resolver-verification-skipped profile=\"{RedactDiagnostic(profileName)}\" reason=\"resolvectl-unavailable\"");
            if (!await IsTunnelDeviceAsync(deviceName, cancellationToken))
            {
                throw new VpnConfigurationException($"NetworkManager exposed physical device {deviceName} instead of a VPN tunnel for {profileName}; refusing to enable traffic.");
            }

            return deviceName;
        }

        if (!status.Success)
        {
            LogCommandFailure("resolver-verification", profileName, "resolvectl status", status);
            throw new VpnConfigurationException($"The system DNS resolver could not be verified for {profileName}; refusing to enable traffic.");
        }

        var globalResolvers = ExtractGlobalResolvectlDnsServers(status.StandardOutput);
        if (globalResolvers.Count > 0 && !HasOnlyPrivateResolver(globalResolvers))
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_resolver=\"{PrivateDnsAddress}\" resolver_state=\"{RedactDiagnostic(status.StandardOutput)}\"");
            throw new VpnConfigurationException($"The system DNS resolver for {profileName} exposes a global non-private DNS server; refusing to enable traffic.");
        }

        if (HasCompetingDefaultDnsRoute(status.StandardOutput, deviceName))
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_resolver=\"{PrivateDnsAddress}\" resolver_state=\"{RedactDiagnostic(status.StandardOutput)}\"");
            throw new VpnConfigurationException($"The system DNS resolver for {profileName} exposes a competing non-private default DNS route; refusing to enable traffic.");
        }

        var parsedStatus = ParseResolvectlStatus(status.StandardOutput);
        var privateDnsLink = parsedStatus.Links.FirstOrDefault(HasPrivateDnsRoute);
        if (privateDnsLink is null)
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_resolver=\"{PrivateDnsAddress}\" expected_domain=\"~.\" resolver_state=\"{RedactDiagnostic(status.StandardOutput)}\"");
            throw new VpnConfigurationException($"The private DNS resolver is not attached to an active VPN link for {profileName}; refusing to enable traffic.");
        }

        if (!await IsTunnelDeviceAsync(privateDnsLink.Name, cancellationToken))
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_resolver=\"{PrivateDnsAddress}\" link=\"{RedactDiagnostic(privateDnsLink.Name)}\" reason=physical-link");
            throw new VpnConfigurationException($"The private DNS resolver for {profileName} is attached to a physical interface; refusing to enable traffic.");
        }

        if (!string.Equals(privateDnsLink.Name, deviceName, StringComparison.Ordinal))
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_link=\"{RedactDiagnostic(deviceName)}\" actual_link=\"{RedactDiagnostic(privateDnsLink.Name)}\" reason=unrelated-tunnel");
            throw new VpnConfigurationException($"The private DNS resolver for {profileName} belongs to a different VPN link; refusing to enable traffic.");
        }

        // GENERAL.DEVICES can be the physical parent of a VPN connection. The resolver link
        // is the authoritative tunnel identity when systemd-resolved is available.
        var tunnelDeviceName = privateDnsLink.Name;
        var linkDns = await _processRunner.RunAsync("resolvectl", ["dns", tunnelDeviceName], cancellationToken);
        if (!linkDns.Success)
        {
            LogCommandFailure("resolver-verification", profileName, "resolvectl dns", linkDns);
            throw new VpnConfigurationException($"The VPN DNS resolver could not be verified for {profileName}; refusing to enable traffic.");
        }

        if (!HasOnlyPrivateResolver(ExtractIpAddresses(linkDns.StandardOutput)))
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_resolver=\"{PrivateDnsAddress}\" link=\"{RedactDiagnostic(tunnelDeviceName)}\" actual_resolvers=\"{RedactDiagnostic(linkDns.StandardOutput)}\"");
            throw new VpnConfigurationException($"The VPN DNS link for {profileName} is not limited to private resolver {PrivateDnsAddress}; refusing to enable traffic.");
        }

        var linkDomain = await _processRunner.RunAsync("resolvectl", ["domain", tunnelDeviceName], cancellationToken);
        if (!linkDomain.Success)
        {
            LogCommandFailure("resolver-verification", profileName, "resolvectl domain", linkDomain);
            throw new VpnConfigurationException($"The VPN DNS routing domain could not be verified for {profileName}; refusing to enable traffic.");
        }

        if (!linkDomain.StandardOutput
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("~.", StringComparer.Ordinal))
        {
            _diagnosticSink($"vpn-resolver-verification-failed profile=\"{RedactDiagnostic(profileName)}\" expected_domain=\"~.\" link=\"{RedactDiagnostic(tunnelDeviceName)}\" actual_domains=\"{RedactDiagnostic(linkDomain.StandardOutput)}\"");
            throw new VpnConfigurationException($"The VPN DNS link for {profileName} does not own the default DNS routing domain; refusing to enable traffic.");
        }

        return tunnelDeviceName;
    }

    private async Task<bool> IsTunnelDeviceAsync(string deviceName, CancellationToken cancellationToken)
    {
        var type = await _processRunner.RunAsync(
            "nmcli",
            ["-g", "GENERAL.TYPE", "device", "show", deviceName],
            cancellationToken);
        if (type.Success)
        {
            var normalizedType = NormalizeNmcliValue(type.StandardOutput);
            if (!string.IsNullOrWhiteSpace(normalizedType)
                && !string.Equals(normalizedType, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return IsTunnelType(normalizedType);
            }
        }

        return IsLikelyTunnelDeviceName(deviceName);
    }

    private static bool IsResolvectlUnavailable(ProcessResult result)
        => result.ExitCode == 127
            || Regex.IsMatch(
                result.StandardError,
                "not found|failed to connect to bus|not been booted with systemd|unit dbus-org.freedesktop.resolve1.service not found",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasOnlyPrivateResolver(IEnumerable<string> resolvers)
    {
        var distinctResolvers = resolvers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return distinctResolvers.Length == 1
            && string.Equals(distinctResolvers[0], PrivateDnsAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExtractGlobalResolvectlDnsServers(string resolverStatus)
        => ParseResolvectlStatus(resolverStatus).GlobalDnsServers;

    private static bool HasCompetingDefaultDnsRoute(string resolverStatus, string vpnDeviceName)
        => ParseResolvectlStatus(resolverStatus)
            .Links
            .Any(link => IsCompetingDefaultDnsRoute(link, vpnDeviceName));

    private static ParsedResolvectlStatus ParseResolvectlStatus(string resolverStatus)
    {
        var globalDnsServers = new List<string>();
        var links = new List<ResolvectlLinkState>();
        ResolvectlLinkState? currentLink = null;
        var inGlobalSection = false;
        var continuingGlobalDnsServers = false;

        foreach (var line in resolverStatus.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            var linkMatch = Regex.Match(
                trimmed,
                @"^Link\s+\d+\s+\((?<name>[^)]+)\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (linkMatch.Success)
            {
                if (currentLink is not null)
                {
                    links.Add(currentLink);
                }

                currentLink = new ResolvectlLinkState(linkMatch.Groups["name"].Value);
                inGlobalSection = false;
                continuingGlobalDnsServers = false;
                continue;
            }

            if (string.Equals(trimmed, "Global", StringComparison.OrdinalIgnoreCase))
            {
                if (currentLink is not null)
                {
                    links.Add(currentLink);
                    currentLink = null;
                }

                inGlobalSection = true;
                continuingGlobalDnsServers = false;
                continue;
            }

            if (currentLink is not null)
            {
                if (trimmed.StartsWith("Current DNS Server:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("DNS Servers:", StringComparison.OrdinalIgnoreCase))
                {
                    AddIpAddressesAfterFirstColon(trimmed, currentLink.DnsServers);
                    currentLink.ContinuingDnsServers = trimmed.StartsWith("DNS Servers:", StringComparison.OrdinalIgnoreCase);
                    currentLink.ContinuingDomains = false;
                    continue;
                }

                if (trimmed.StartsWith("DNS Domain:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("DNS Domains:", StringComparison.OrdinalIgnoreCase))
                {
                    AddTokensAfterFirstColon(trimmed, currentLink.Domains);
                    currentLink.ContinuingDomains = true;
                    currentLink.ContinuingDnsServers = false;
                    continue;
                }

                if (line.Length > trimmed.Length && !trimmed.Contains(':', StringComparison.Ordinal))
                {
                    if (currentLink.ContinuingDnsServers)
                    {
                        currentLink.DnsServers.AddRange(ExtractIpAddresses(trimmed));
                        continue;
                    }

                    if (currentLink.ContinuingDomains)
                    {
                        AddTokens(trimmed, currentLink.Domains);
                        continue;
                    }
                }

                currentLink.ContinuingDnsServers = false;
                currentLink.ContinuingDomains = false;
                continue;
            }

            if (!inGlobalSection)
            {
                continue;
            }

            if (trimmed.StartsWith("Fallback DNS Servers:", StringComparison.OrdinalIgnoreCase))
            {
                continuingGlobalDnsServers = false;
                continue;
            }

            if (trimmed.StartsWith("Current DNS Server:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("DNS Servers:", StringComparison.OrdinalIgnoreCase))
            {
                AddIpAddressesAfterFirstColon(trimmed, globalDnsServers);
                continuingGlobalDnsServers = trimmed.StartsWith("DNS Servers:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (continuingGlobalDnsServers
                && line.Length > trimmed.Length
                && !trimmed.Contains(':', StringComparison.Ordinal))
            {
                globalDnsServers.AddRange(ExtractIpAddresses(trimmed));
                continue;
            }

            continuingGlobalDnsServers = false;
        }

        if (currentLink is not null)
        {
            links.Add(currentLink);
        }

        return new ParsedResolvectlStatus(globalDnsServers, links);
    }

    private static bool HasPrivateDnsRoute(ResolvectlLinkState link)
        => HasOnlyPrivateResolver(link.DnsServers)
            && link.Domains.Contains("~.", StringComparer.Ordinal);

    private static bool IsCompetingDefaultDnsRoute(ResolvectlLinkState? link, string vpnDeviceName)
        => link is not null
            && !string.Equals(link.Name, vpnDeviceName, StringComparison.Ordinal)
            && link.Domains.Contains("~.", StringComparer.Ordinal)
            && link.DnsServers.Count > 0
            && !HasOnlyPrivateResolver(link.DnsServers);

    private static void AddIpAddressesAfterFirstColon(string value, ICollection<string> destination)
    {
        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex >= 0)
        {
            foreach (var address in ExtractIpAddresses(value[(colonIndex + 1)..]))
            {
                destination.Add(address);
            }
        }
    }

    private static void AddTokensAfterFirstColon(string value, ICollection<string> destination)
    {
        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex >= 0)
        {
            AddTokens(value[(colonIndex + 1)..], destination);
        }
    }

    private static void AddTokens(string value, ICollection<string> destination)
    {
        foreach (var token in value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            destination.Add(token);
        }
    }

    private sealed class ResolvectlLinkState(string name)
    {
        public string Name { get; } = name;
        public List<string> DnsServers { get; } = [];
        public List<string> Domains { get; } = [];
        public bool ContinuingDnsServers { get; set; }
        public bool ContinuingDomains { get; set; }
    }

    private sealed record ParsedResolvectlStatus(
        IReadOnlyList<string> GlobalDnsServers,
        IReadOnlyList<ResolvectlLinkState> Links);

    private static IReadOnlyList<string> ExtractIpAddresses(string value)
        => value
            .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('(', ')', '[', ']', '{', '}', '\"'))
            .Where(token => (token.Contains('.') || token.Contains(':')) && IPAddress.TryParse(token, out _))
            .ToArray();

    private static bool RouteUsesDevice(string routeOutput, string expectedDevice)
    {
        var tokens = routeOutput.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (string.Equals(tokens[index], "dev", StringComparison.Ordinal)
                && string.Equals(tokens[index + 1], expectedDevice, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> GetLibreGuardProfilesAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        var arguments = activeOnly
            ? new[] { "-t", "-f", "NAME,TYPE", "connection", "show", "--active" }
            : ["-t", "-f", "NAME,TYPE", "connection", "show"];
        var show = await _processRunner.RunAsync("nmcli", arguments, cancellationToken);
        if (!show.Success)
        {
            return [];
        }

        return show.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseConnectionLine)
            .Where(connection => connection is not null)
            .Select(connection => connection!)
            .Where(connection => string.Equals(connection.Type, "vpn", StringComparison.OrdinalIgnoreCase))
            .Select(connection => connection.Name)
            .Where(IsLibreGuardProfileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ParsedConnection? ParseConnectionLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separatorIndex = line.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
        {
            return null;
        }

        return new ParsedConnection(
            line[..separatorIndex],
            line[(separatorIndex + 1)..]);
    }

    private static bool IsLibreGuardProfileName(string profileName)
        => LibreGuardProfilePrefixes.Any(prefix => profileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipProfile(string profileName, string? excludeProfileName)
        => !string.IsNullOrWhiteSpace(excludeProfileName)
            && string.Equals(profileName, excludeProfileName, StringComparison.OrdinalIgnoreCase);

    private async Task RepairIkeV2RoutingRuleAsync(CancellationToken cancellationToken)
    {
        var rules = await _processRunner.RunAsync("ip", ["rule", "show"], cancellationToken);
        if (!rules.Success || !HasUnconditionalTable220Rule(rules.StandardOutput))
        {
            return;
        }

        if (HasInstalledRouteRepairLifecycle())
        {
            for (var attempt = 1; attempt <= InstalledRouteRepairWaitAttempts; attempt++)
            {
                await _delay(InstalledRouteRepairPollInterval, cancellationToken);
                var repairedRules = await _processRunner.RunAsync("ip", ["rule", "show"], cancellationToken);
                if (!repairedRules.Success)
                {
                    break;
                }

                if (!HasUnconditionalTable220Rule(repairedRules.StandardOutput))
                {
                    _diagnosticSink($"vpn-ikev2-route-repair source=networkmanager-dispatcher wait_attempt={attempt}");
                    return;
                }
            }
        }

        var delete = await DeleteUnconditionalTable220RuleAsync("ip", ["rule", "del", "pref", "220", "from", "all", "lookup", "220"], cancellationToken);
        if (delete.Success || IsMissingRule(delete.StandardError))
        {
            return;
        }

        if (!RequiresElevatedRouteRepair(delete.StandardError))
        {
            throw CreateRouteRepairException();
        }

        if (!_fileExists(RouteRepairHelperPath))
        {
            throw CreateMissingRouteRepairHelperException();
        }

        var elevatedDelete = await DeleteUnconditionalTable220RuleAsync("pkexec", [RouteRepairHelperPath], cancellationToken);
        if (elevatedDelete.Success || IsMissingRule(elevatedDelete.StandardError))
        {
            return;
        }

        if (IsAuthorizationDismissed(elevatedDelete.StandardError))
        {
            throw CreateRouteRepairAuthorizationDismissedException();
        }

        throw CreateRouteRepairException();
    }

    private bool HasInstalledRouteRepairLifecycle()
        => _fileExists(RouteRepairHelperPath)
            && (_fileExists(SystemPreUpDispatcherPath)
                || _fileExists(VendorPreUpDispatcherPath)
                || _fileExists(VendorDispatcherPath));

    private Task<ProcessResult> DeleteUnconditionalTable220RuleAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
        => _processRunner.RunAsync(fileName, arguments, cancellationToken);

    private static bool HasUnconditionalTable220Rule(string rules)
        => Regex.IsMatch(rules, @"(?m)^\s*220:\s+from\s+all\s+lookup\s+220\s*$", RegexOptions.CultureInvariant);

    private static bool IsMissingRule(string error)
        => Regex.IsMatch(error, "No such process|No such file|Cannot find", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool RequiresElevatedRouteRepair(string error)
        => Regex.IsMatch(error, "Operation not permitted|Permission denied|not authorized|Need to be root", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsAuthorizationDismissed(string error)
        => Regex.IsMatch(error, "Request dismissed|authentication.*dismissed|authorization.*dismissed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static VpnConfigurationException CreateRouteRepairException()
        => new("IKEv2 connected, but LibreGuard could not repair the broken strongSwan routing rule (`from all lookup 220`) that prevents traffic from entering the VPN tunnel.");

    private static VpnConfigurationException CreateMissingRouteRepairHelperException()
        => new($"IKEv2 connected, but LibreGuard needs its one-time Linux privilege setup before it can repair the broken strongSwan routing rule (`from all lookup 220`). Run `sudo ./install-linux-privileges.sh` from the published app directory so `{RouteRepairHelperPath}` is installed, then try again.");

    private static VpnConfigurationException CreateRouteRepairAuthorizationDismissedException()
        => new($"IKEv2 connected, but route repair authorization was dismissed. Retry the connection and approve the LibreGuard Polkit authorization prompt so traffic can enter the VPN tunnel.");

    private static string CreateIkeV2InterfaceName()
        => $"lgvpn{Guid.NewGuid():N}"[..13];

    private static IReadOnlyList<string> SplitVpnDataItems(string vpnData)
    {
        var items = new List<string>();
        var start = 0;
        var escaped = false;
        for (var index = 0; index < vpnData.Length; index++)
        {
            var ch = vpnData[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == ',')
            {
                AddVpnDataItem(vpnData[start..index], items);
                start = index + 1;
            }
        }

        AddVpnDataItem(vpnData[start..], items);
        return items;
    }

    private static void AddVpnDataItem(string item, List<string> items)
    {
        item = item.Trim();
        if (!string.IsNullOrWhiteSpace(item))
        {
            items.Add(item);
        }
    }

    private static void ValidateIkeV2VpnData(IReadOnlyList<string> vpnDataItems)
    {
        var keys = vpnDataItems.Select(GetVpnDataKey).ToHashSet(StringComparer.Ordinal);
        var missing = new[] { "address", "usercert", "userkey", "certificate", "method", "remote-ts" }
            .Where(key => !keys.Contains(key))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new VpnConfigurationException($"IKEv2 profile is missing NetworkManager strongSwan setting(s): {string.Join(", ", missing)}.");
        }

        var remoteTrafficSelectors = vpnDataItems
            .Where(item => string.Equals(GetVpnDataKey(item), "remote-ts", StringComparison.Ordinal))
            .Select(GetVpnDataValue)
            .FirstOrDefault();
        var method = vpnDataItems
            .Where(item => string.Equals(GetVpnDataKey(item), "method", StringComparison.Ordinal))
            .Select(GetVpnDataValue)
            .FirstOrDefault();
        if (!HasRequiredIpv4TrafficSelector(remoteTrafficSelectors)
            || !string.Equals(DecodeVpnDataValue(method), "key", StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnConfigurationException("IKEv2 profile must request the IPv4 full-tunnel traffic selector 0.0.0.0/0.");
        }
    }

    private static void ValidateIkeV2RemoteAddress(IReadOnlyList<string> vpnDataItems, string expectedRemoteAddress)
    {
        var address = vpnDataItems
            .Where(item => string.Equals(GetVpnDataKey(item), "address", StringComparison.Ordinal))
            .Select(GetVpnDataValue)
            .FirstOrDefault();
        if (!IsExpectedRemoteAddress(address, expectedRemoteAddress))
        {
            throw new VpnConfigurationException("IKEv2 profile must preserve the backend-provided remote address.");
        }
    }

    private static void ValidateIkeV2ProfileWasStored(
        string profileData,
        string expectedRemoteAddress)
    {
        var missing = new[] { "address", "usercert", "userkey", "certificate", "method", "remote-ts" }
            .Where(key => !ContainsVpnDataKey(profileData, key))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new VpnConfigurationException($"NetworkManager did not store IKEv2 strongSwan setting(s): {string.Join(", ", missing)}.");
        }

        var remoteTrafficSelectors = ExtractVpnDataValue(profileData, "remote-ts");
        var method = ExtractVpnDataValue(profileData, "method");
        var address = ExtractVpnDataValue(profileData, "address");
        if (!HasRequiredIpv4TrafficSelector(remoteTrafficSelectors)
            || !IsExpectedRemoteAddress(address, expectedRemoteAddress)
            || !string.Equals(DecodeVpnDataValue(method), "key", StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnConfigurationException("NetworkManager did not store the required backend remote address and IPv4-only IKEv2 traffic selector.");
        }
    }

    private static string GetVpnDataKey(string item)
    {
        var equals = item.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? item.Trim() : item[..equals].Trim();
    }

    private static string GetVpnDataValue(string item)
    {
        var equals = item.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? string.Empty : item[(equals + 1)..].Trim();
    }

    private static bool ContainsVpnDataKey(string profileData, string key)
        => Regex.IsMatch(profileData, $@"(^|[,\s]){Regex.Escape(key)}\s*=", RegexOptions.CultureInvariant);

    private static string? ExtractVpnDataValue(string profileData, string key)
    {
        var match = Regex.Match(
            profileData,
            $@"(?:^|[,\s]){Regex.Escape(key)}\s*=\s*(?<value>.*?)(?=,\s*[A-Za-z][A-Za-z0-9-]*\s*=|$)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static bool HasRequiredIpv4TrafficSelector(string? value)
    {
        var selectors = DecodeVpnDataValue(value)
            ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        return selectors.SetEquals(["0.0.0.0/0"]);
    }

    private static string ReplaceIkeV2CertificatePath(string? vpnData, string certificatePath)
    {
        if (string.IsNullOrWhiteSpace(vpnData))
        {
            throw new VpnConfigurationException("IKEv2 profile did not produce NetworkManager strongSwan data for a gateway certificate retry.");
        }

        var escapedPath = EscapeVpnDataValue(certificatePath);
        var items = SplitVpnDataItems(vpnData)
            .Select(item => string.Equals(GetVpnDataKey(item), "certificate", StringComparison.Ordinal)
                ? $"certificate={escapedPath}"
                : item)
            .ToArray();
        return string.Join(',', items);
    }

    private static void ValidateStoredIkeV2CertificatePath(string profileData, string expectedPath)
    {
        var actualPath = DecodeVpnDataValue(ExtractVpnDataValue(profileData, "certificate"));
        if (!string.Equals(actualPath, expectedPath, StringComparison.Ordinal))
        {
            throw new VpnConfigurationException("NetworkManager did not store the requested IKEv2 gateway certificate path.");
        }
    }

    private static string EscapeVpnDataValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    private static bool IsUuid(string value)
        => Guid.TryParse(value, out _);

    private static string ParseFirstDevice(string value)
        => value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeNmcliValue)
            .FirstOrDefault(device => !string.IsNullOrWhiteSpace(device))
            ?? string.Empty;

    private static bool IsExpectedRemoteAddress(string? value, string expectedRemoteAddress)
        => string.Equals(
            DecodeVpnDataValue(value),
            expectedRemoteAddress,
            StringComparison.Ordinal);

    private static string? DecodeVpnDataValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var decoded = value;
        for (var pass = 0; pass < 4; pass++)
        {
            var builder = new StringBuilder(decoded.Length);
            var escaped = false;
            foreach (var ch in decoded)
            {
                if (escaped)
                {
                    builder.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                builder.Append(ch);
            }

            if (escaped)
            {
                builder.Append('\\');
            }

            var next = builder.ToString();
            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                return next;
            }

            decoded = next;
        }

        return decoded;
    }

    private static string? ParseImportedConnectionName(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = Regex.Match(output, "Connection\\s+'(?<name>[^']+)'", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private sealed record ParsedConnection(string Name, string Type);
}
