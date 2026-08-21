using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class NetworkManagerClientTests
{
    private const string RouteRepairHelperPath = "/usr/libexec/libreguard-vpn-linux/libreguard-ikev2-route-repair";
    private static readonly (string Name, string Value)[] ExpectedFullTunnelSettings =
    [
        ("ipv4.never-default", "no"),
        ("ipv4.ignore-auto-routes", "no"),
        ("ipv6.never-default", "no"),
        ("ipv6.ignore-auto-routes", "no")
    ];
    private static readonly (string Name, string Value)[] ExpectedIkeV2FullTunnelSettings =
    [
        ("ipv4.never-default", "no"),
        ("ipv4.ignore-auto-routes", "no"),
        ("ipv6.never-default", "yes"),
        ("ipv6.ignore-auto-routes", "yes")
    ];
    private static readonly (string Name, string Value)[] ExpectedPrivateDnsSettings =
    [
        ("ipv4.dns", "10.254.0.53"),
        ("ipv4.dns-search", "~."),
        ("ipv4.routed-dns", "yes"),
        ("ipv4.ignore-auto-dns", "yes"),
        ("ipv4.dns-priority", "-2147483648"),
        ("ipv6.dns", string.Empty),
        ("ipv6.dns-search", string.Empty),
        ("ipv6.ignore-auto-dns", "yes"),
        ("ipv6.dns-priority", "-2147483648")
    ];
    private static readonly (string Name, string Value)[] ExpectedLegacyPrivateDnsSettings =
    [
        ("ipv4.routes", "10.254.0.53/32")
    ];

    [Fact]
    public async Task ImportOpenVpnAsync_ConfiguresAndVerifiesPrivateDnsOnFinalProfileName()
    {
        var runner = new RecordingRunner
        {
            OpenVpnImportOutput = "Connection 'imported-openvpn' (9b96d992) successfully added."
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        await client.ImportOpenVpnAsync(profile, CancellationToken.None);

        var dnsIndex = AssertPrivateDnsConfiguredAndVerified(runner, profile.ProfileName);
        AssertFullTunnelConfiguredAndVerified(runner, profile.ProfileName);
        Assert.Contains(runner.Commands, command => HasOption(command.Arguments, "connection.id", profile.ProfileName));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("connection.permissions"));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("+ipv4.dns"));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "verify", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_UsesPrivateResolverRouteOnOlderNetworkManager()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.46.0"
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        await client.ImportOpenVpnAsync(profile, CancellationToken.None);

        Assert.Contains(runner.Commands, command => HasOption(command.Arguments, "ipv4.routes", "10.254.0.53/32"));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("ipv4.routed-dns"));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_AcceptsTerseEmptyDnsValues()
    {
        var runner = new RecordingRunner
        {
            EmptyConfiguredProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "ipv6.dns",
                "ipv6.dns-search"
            }
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        await client.ImportOpenVpnAsync(profile, CancellationToken.None);
    }

    [Fact]
    public async Task ImportOpenVpnAsync_FailsClosedWhenNetworkManagerVersionCannotBeParsed()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool"
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportOpenVpnAsync(profile, CancellationToken.None));

        Assert.Contains("determine the installed NetworkManager version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("import"));
    }

    [Fact]
    public async Task ImportIkeV2Async_AppendsVpnDataOneItemAtATimeForNetworkManager146()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(runner);
        var profile = new VpnProfile(
            VpnProtocol.Ikev2,
            "libreguard-ike",
            "/tmp/libreguard-ike.sswan",
            null,
            "address=198.51.100.1,usercert=/tmp/client.crt,userkey=/tmp/client.key,certificate=/tmp/gateway.crt,method=key,virtual=yes,remote-ts=0.0.0.0/0",
            "198.51.100.1");

        await client.ImportIkeV2Async(profile, CancellationToken.None);

        var addCommand = Assert.Single(runner.Commands, command => command.Arguments.Contains("add") && command.Arguments.Contains("vpn-type"));
        var ifnameIndex = addCommand.Arguments.ToList().IndexOf("ifname");
        Assert.True(ifnameIndex >= 0);
        Assert.StartsWith("lgvpn", addCommand.Arguments[ifnameIndex + 1]);
        Assert.NotEqual("--", addCommand.Arguments[ifnameIndex + 1]);
        Assert.Contains(runner.Commands, command => HasOption(command.Arguments, "ipv4.never-default", "no"));
        Assert.Contains(runner.Commands, command => HasOption(command.Arguments, "ipv4.ignore-auto-routes", "no"));
        Assert.Contains(runner.Commands, command => HasOption(command.Arguments, "ipv6.never-default", "yes"));
        Assert.Contains(runner.Commands, command => HasOption(command.Arguments, "ipv6.ignore-auto-routes", "yes"));
        var configurationCommand = Assert.Single(runner.Commands, command =>
            command.Arguments.Count >= 2
            && command.Arguments[0] == "connection"
            && command.Arguments[1] == "modify"
            && command.Arguments.Contains("vpn.data"));
        var vpnDataIndex = configurationCommand.Arguments.ToList().IndexOf("vpn.data");
        Assert.Equal(string.Empty, configurationCommand.Arguments[vpnDataIndex + 1]);
        var appendedVpnData = runner.Commands
            .Where(command => command.Arguments.Count == 5
                && command.Arguments[0] == "connection"
                && command.Arguments[1] == "modify"
                && command.Arguments[3] == "+vpn.data")
            .Select(command => command.Arguments[4])
            .ToArray();
        Assert.Equal(7, appendedVpnData.Length);
        Assert.Contains("address=198.51.100.1", appendedVpnData);
        Assert.Contains("usercert=/tmp/client.crt", appendedVpnData);
        Assert.Contains("userkey=/tmp/client.key", appendedVpnData);
        Assert.Contains("certificate=/tmp/gateway.crt", appendedVpnData);
        Assert.Contains("remote-ts=0.0.0.0/0", appendedVpnData);
        Assert.Contains(runner.Commands, command => command.Arguments.Contains("-g") && command.Arguments.Contains("vpn.data"));
        var vpnVerificationIndex = runner.Commands.FindIndex(command => command.Arguments.SequenceEqual(["-g", "vpn.data", "connection", "show", profile.ProfileName]));
        var dnsIndex = AssertPrivateDnsConfiguredAndVerified(runner, profile.ProfileName);
        AssertFullTunnelConfiguredAndVerified(runner, profile.ProfileName, ExpectedIkeV2FullTunnelSettings);
        Assert.True(dnsIndex < vpnVerificationIndex);
        Assert.Equal(7, runner.Commands.Count(command => command.Arguments.Contains("+vpn.data")));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("+ipv4.dns"));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "verify", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportIkeV2Async_AssignsTheProfileToTheCurrentUserForPrivateCredentialAccess()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(runner);
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);

        Assert.Contains(runner.Commands, command =>
            command.Arguments.Count >= 3
            && command.Arguments[0] == "connection"
            && command.Arguments[1] == "modify"
            && HasOption(command.Arguments, "connection.permissions", $"user:{Environment.UserName}"));
    }

    [Fact]
    public async Task ActivateIkeV2Async_OnFedoraNetworkManager156TemporarilyBypassesBrokenCredentialHelper()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.56.0"
        };
        var diagnostics = new List<string>();
        var client = new NetworkManagerClient(
            runner,
            fileExists: path => path == "/etc/os-release",
            diagnosticSink: diagnostics.Add,
            readAllText: path => path == "/etc/os-release" ? "ID=fedora\nVERSION_ID=44\n" : string.Empty,
            userHomeDirectory: "/tmp");
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        await client.ActivateAsync(profile, CancellationToken.None);

        var clearPermissionIndex = runner.Commands.FindIndex(command =>
            command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "connection.permissions", string.Empty]));
        var activationIndex = runner.Commands.FindIndex(command =>
            command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        var restoredPermissionIndex = runner.Commands.FindLastIndex(command =>
            command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "connection.permissions", $"user:{Environment.UserName}"]));
        var firstAclGrantIndex = runner.Commands.FindIndex(command =>
            command.FileName == "setfacl" && command.Arguments.Contains("--modify"));
        var lastAclRestoreIndex = runner.Commands.FindLastIndex(command =>
            command.FileName == "setfacl" && command.Arguments.Contains("--set"));
        var aclGrantCount = runner.Commands.Count(command =>
            command.FileName == "setfacl" && command.Arguments.Contains("--modify"));
        var aclRestoreCount = runner.Commands.Count(command =>
            command.FileName == "setfacl" && command.Arguments.Contains("--set"));

        Assert.True(firstAclGrantIndex >= 0);
        Assert.True(firstAclGrantIndex < clearPermissionIndex);
        Assert.True(clearPermissionIndex >= 0);
        Assert.True(clearPermissionIndex < activationIndex);
        Assert.True(activationIndex < restoredPermissionIndex);
        Assert.True(restoredPermissionIndex < lastAclRestoreIndex);
        Assert.Equal(aclGrantCount, aclRestoreCount);
        Assert.Contains(diagnostics, message => message.Contains("state=enabled", StringComparison.Ordinal));
        Assert.Contains(diagnostics, message => message.Contains("state=restored-private", StringComparison.Ordinal));
        Assert.Contains(diagnostics, message => message.Contains("credential_acl=uid0-temporary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivateIkeV2Async_OnFedoraNetworkManager156RestoresPrivatePermissionAfterFailure()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.56.0",
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Error: Connection activation failed: No valid secrets")
        };
        var client = new NetworkManagerClient(
            runner,
            fileExists: path => path == "/etc/os-release",
            readAllText: path => path == "/etc/os-release" ? "ID=fedora\nVERSION_ID=44\n" : string.Empty,
            userHomeDirectory: "/tmp");
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(profile, CancellationToken.None));

        var activationIndex = runner.Commands.FindIndex(command =>
            command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        var restoredPermissionIndex = runner.Commands.FindLastIndex(command =>
            command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "connection.permissions", $"user:{Environment.UserName}"]));

        Assert.True(activationIndex >= 0);
        Assert.True(activationIndex < restoredPermissionIndex);
        Assert.Equal(
            runner.Commands.Count(command => command.FileName == "setfacl" && command.Arguments.Contains("--modify")),
            runner.Commands.Count(command => command.FileName == "setfacl" && command.Arguments.Contains("--set")));
    }

    [Fact]
    public async Task ActivateIkeV2Async_OnFedoraNetworkManager156RollsBackPartialAclBeforePermissionChange()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.56.0",
            SetfaclGrantFailurePath = Path.GetFullPath("/tmp/client.crt")
        };
        var client = new NetworkManagerClient(
            runner,
            fileExists: path => path == "/etc/os-release",
            readAllText: path => path == "/etc/os-release" ? "ID=fedora\nVERSION_ID=44\n" : string.Empty,
            userHomeDirectory: "/tmp");
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(profile, CancellationToken.None));

        Assert.DoesNotContain(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "connection.permissions", string.Empty]));
        Assert.DoesNotContain(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.Equal(
            runner.Commands.Count(command => command.FileName == "setfacl" && command.Arguments.Contains("--modify")),
            runner.Commands.Count(command => command.FileName == "setfacl" && command.Arguments.Contains("--set")));
    }

    [Fact]
    public async Task ActivateIkeV2Async_OnFedoraNetworkManager156RejectsCredentialOutsideHome()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.56.0"
        };
        var client = new NetworkManagerClient(
            runner,
            fileExists: path => path == "/etc/os-release",
            readAllText: path => path == "/etc/os-release" ? "ID=fedora\nVERSION_ID=44\n" : string.Empty,
            userHomeDirectory: "/tmp");
        var profile = CreateIkeV2Profile(credentialPaths: ["/var/tmp/client.key"]);

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(profile, CancellationToken.None));

        Assert.Contains("outside the current user's home", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command =>
            command.FileName is "getfacl" or "setfacl");
        Assert.DoesNotContain(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "connection.permissions", string.Empty]));
    }

    [Fact]
    public async Task ActivateIkeV2Async_OnDebianNetworkManager156KeepsPrivatePermissionThroughout()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.56.0"
        };
        var client = new NetworkManagerClient(
            runner,
            fileExists: path => path == "/etc/os-release",
            readAllText: path => path == "/etc/os-release" ? "ID=debian\nVERSION_ID=13\n" : string.Empty);
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        await client.ActivateAsync(profile, CancellationToken.None);

        Assert.DoesNotContain(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "connection.permissions", string.Empty]));
        Assert.Single(runner.Commands, command =>
            command.Arguments.Contains("connection.permissions")
            && command.Arguments.Contains($"user:{Environment.UserName}"));
        Assert.DoesNotContain(runner.Commands, command =>
            command.FileName is "getfacl" or "setfacl");
    }

    [Fact]
    public async Task ImportThenActivateAsync_ProceedsForOpenVpnWithoutUnsupportedVerificationCommand()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        await client.ImportOpenVpnAsync(profile, CancellationToken.None);
        await client.ActivateAsync(profile, CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "verify", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportThenActivateAsync_ProceedsForIkeV2WithoutUnsupportedVerificationCommand()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(runner);
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        await client.ActivateAsync(profile, CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "verify", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportThenActivateAsync_AcceptsNetworkManager146EscapedIkeSelectorsAndExpandedPrivateDnsRoute()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.46.0",
            VpnDataReadback = "address = 198.51.100.1, usercert = /tmp/client.crt, userkey = /tmp/client.key, certificate = /tmp/gateway.crt, method = key, remote-ts = 0.0.0.0/0",
            ConfiguredSettingOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ipv4.routes"] = "10.254.0.53/32 0.0.0.0 50"
            }
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateIkeV2Profile();

        await client.ImportIkeV2Async(profile, CancellationToken.None);
        await client.ActivateAsync(profile, CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "verify", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_NormalizesNetworkManagerBooleanAndDnsListReadback()
    {
        var runner = new RecordingRunner
        {
            ConfiguredSettingOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ipv4.dns"] = "10.254.0.53, 10.254.0.53",
                ["ipv4.ignore-auto-dns"] = "true",
                ["ipv4.routed-dns"] = "1",
                ["ipv6.ignore-auto-dns"] = "true",
                ["ipv4.never-default"] = "false",
                ["ipv4.ignore-auto-routes"] = "0",
                ["ipv6.never-default"] = "false",
                ["ipv6.ignore-auto-routes"] = "0"
            }
        };
        var client = new NetworkManagerClient(runner);

        await client.ImportOpenVpnAsync(CreateOpenVpnProfile(), CancellationToken.None);
    }

    [Theory]
    [InlineData("::/0")]
    [InlineData("0.0.0.0/0;::/0")]
    [InlineData("0.0.0.0/0;::/0;10.0.0.0/8")]
    public async Task ImportIkeV2Async_RejectsPartialOrExtraStoredTrafficSelectors(string storedSelectors)
    {
        var runner = new RecordingRunner
        {
            VpnDataReadback = $"address = 198.51.100.1, usercert = /tmp/client.crt, userkey = /tmp/client.key, certificate = /tmp/gateway.crt, method = key, remote-ts = {storedSelectors}"
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ImportIkeV2Async(CreateIkeV2Profile(), CancellationToken.None));

        Assert.Contains("IPv4-only IKEv2 traffic selector", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", "libreguard-ike"]));
    }

    [Fact]
    public async Task ImportIkeV2Async_PreservesBackendHostnameRemoteAddress()
    {
        var runner = new RecordingRunner
        {
            VpnDataReadback = "address = vpn.example.com, usercert = /tmp/client.crt, userkey = /tmp/client.key, certificate = /tmp/gateway.crt, method = key, remote-ts = 0.0.0.0/0"
        };
        var client = new NetworkManagerClient(runner);

        var profile = CreateIkeV2Profile(remoteAddress: "vpn.example.com");
        await client.ImportIkeV2Async(profile, CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "modify", profile.ProfileName, "+vpn.data", "address=vpn.example.com"]));
    }

    [Fact]
    public async Task ImportIkeV2Async_RejectsCommaSeparatedSelectorsBeforeActivation()
    {
        var runner = new RecordingRunner();
        var profile = new VpnProfile(
            VpnProtocol.Ikev2,
            "libreguard-ike",
            "/tmp/libreguard-ike.sswan",
            null,
            "address=198.51.100.1,usercert=/tmp/client.crt,userkey=/tmp/client.key,certificate=/tmp/gateway.crt,method=key,virtual=yes,remote-ts=0.0.0.0/0\\,::/0",
            "198.51.100.1");
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ImportIkeV2Async(profile, CancellationToken.None));

        Assert.Contains("IPv4 full-tunnel", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("add"));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_OnNetworkManager146RejectsMissingPrivateResolverHostRoute()
    {
        var runner = new RecordingRunner
        {
            NmcliVersionOutput = "nmcli tool, version 1.46.0",
            ConfiguredSettingOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ipv4.routes"] = "10.254.0.0/24 0.0.0.0 50"
            }
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ImportOpenVpnAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("ipv4.routes", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", "libreguard-openvpn"]));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_FailsWhenPrivateDnsModificationFails()
    {
        var runner = new RecordingRunner
        {
            PrivateDnsModifyResult = new ProcessResult(10, string.Empty, "modify failed")
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportOpenVpnAsync(profile, CancellationToken.None));

        Assert.Contains("configure NetworkManager profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => IsPrivateDnsQuery(command.Arguments));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_DeletesTheGeneratedAndImportedProfilesWhenProvisioningFails()
    {
        var runner = new RecordingRunner
        {
            OpenVpnImportOutput = "Connection 'imported-openvpn' (9b96d992) successfully added.",
            PrivateDnsModifyResult = new ProcessResult(10, string.Empty, "modify failed")
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportOpenVpnAsync(profile, CancellationToken.None));

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "delete", "imported-openvpn"]));
        Assert.True(runner.Commands.Count(command => command.Arguments.SequenceEqual(["connection", "delete", profile.ProfileName])) >= 2);
    }

    [Fact]
    public async Task ImportOpenVpnAsync_RedactsSensitiveNetworkManagerErrorsInDiagnostics()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner
        {
            PrivateDnsModifyResult = new ProcessResult(10, string.Empty, "vpn.data=secret-value password=top-secret")
        };
        var client = new NetworkManagerClient(runner, _ => true, diagnostics.Add);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportOpenVpnAsync(profile, CancellationToken.None));

        var diagnostic = Assert.Single(diagnostics, entry => entry.Contains("profile-configuration", StringComparison.Ordinal));
        Assert.Contains("exit_code=10", diagnostic, StringComparison.Ordinal);
        Assert.Contains("property=\"ipv4.routed-dns\"", diagnostic, StringComparison.Ordinal);
        Assert.Contains("<redacted>", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportIkeV2Async_FailsWhenPrivateDnsVerificationQueryFails()
    {
        var runner = new RecordingRunner
        {
            PrivateDnsQueryFailureProperty = "ipv6.dns-search"
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateIkeV2Profile();

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportIkeV2Async(profile, CancellationToken.None));

        Assert.Contains("verify NetworkManager setting", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["-g", "ipv6.dns-search", "connection", "show", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportOpenVpnAsync_FailsWhenStoredPrivateDnsDoesNotMatch()
    {
        var runner = new RecordingRunner
        {
            PrivateDnsMismatchProperty = "ipv4.dns"
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateOpenVpnProfile("libreguard-openvpn-nl-1");

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportOpenVpnAsync(profile, CancellationToken.None));

        Assert.Contains("ipv4.dns", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
    }

    [Fact]
    public async Task ImportIkeV2Async_FailsBeforeActivation_WhenRequiredVpnDataIsMissing()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(runner);
        var profile = new VpnProfile(
            VpnProtocol.Ikev2,
            "libreguard-ike",
            "/tmp/libreguard-ike.sswan",
            null,
            "address=vpn.example.com,method=key",
            "198.51.100.1");

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportIkeV2Async(profile, CancellationToken.None));

        Assert.Contains("usercert", ex.Message);
        Assert.Contains("userkey", ex.Message);
    }

    [Fact]
    public async Task ImportIkeV2Async_FailsBeforeActivation_WhenGatewayCertificateIsMissing()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(runner);
        var profile = new VpnProfile(
            VpnProtocol.Ikev2,
            "libreguard-ike",
            "/tmp/libreguard-ike.sswan",
            null,
            "address=vpn.example.com,usercert=/tmp/client.crt,userkey=/tmp/client.key,method=key",
            "198.51.100.1");

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ImportIkeV2Async(profile, CancellationToken.None));

        Assert.Contains("certificate", ex.Message);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("+vpn.data"));
    }

    [Fact]
    public async Task ActivateAsync_RemovesBrokenIkeV2Table220Rule()
    {
        var runner = new RecordingRunner
        {
            IpRuleOutput = """
                0:	from all lookup local
                220:	from all lookup 220
                220:	not from all fwmark 0xdc lookup 220
                32766:	from all lookup main
                32767:	from all lookup default
                """
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateIkeV2Profile(), CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.FileName == "ip" && IsDeleteBrokenTable220Rule(command.Arguments));
        Assert.DoesNotContain(runner.Commands, command => command.FileName == "pkexec");
    }

    [Fact]
    public async Task ActivateAsync_UsesPkexecWhenIkeV2RuleRepairNeedsPrivileges()
    {
        var runner = new RecordingRunner
        {
            IpRuleOutput = "220:\tfrom all lookup 220",
            DirectIpRuleDeleteResult = new ProcessResult(2, string.Empty, "RTNETLINK answers: Operation not permitted")
        };
        var client = new NetworkManagerClient(runner, _ => true, delay: (_, _) => Task.CompletedTask);

        await client.ActivateAsync(CreateIkeV2Profile(), CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.FileName == "ip" && IsDeleteBrokenTable220Rule(command.Arguments));
        Assert.Contains(runner.Commands, command => command.FileName == "pkexec" && command.Arguments.SequenceEqual([RouteRepairHelperPath]));
    }

    [Fact]
    public async Task ActivateAsync_ExplainsWhenPolkitAuthorizationIsDismissed()
    {
        var runner = new RecordingRunner
        {
            IpRuleOutput = "220:\tfrom all lookup 220",
            DirectIpRuleDeleteResult = new ProcessResult(2, string.Empty, "RTNETLINK answers: Operation not permitted"),
            ElevatedRouteRepairResult = new ProcessResult(126, string.Empty, "Error executing command as another user: Request dismissed")
        };
        var client = new NetworkManagerClient(runner, _ => true, delay: (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ActivateAsync(CreateIkeV2Profile(), CancellationToken.None));

        Assert.Contains("authorization was dismissed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approve the LibreGuard Polkit authorization prompt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRepairRoutesForOpenVpn()
    {
        var runner = new RecordingRunner
        {
            IpRuleOutput = "220:\tfrom all lookup 220"
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.DoesNotContain(runner.Commands, command => command.FileName == "ip"
            && command.Arguments.SequenceEqual(["rule", "show"]));
        Assert.DoesNotContain(runner.Commands, command => command.FileName == "pkexec");
    }

    [Fact]
    public async Task ActivateAsync_VerifiesPrivateDnsAndFullTunnelRoutesThroughVpnDevice()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = "tun0",
            ResolvectlStatusResult = new ProcessResult(0, """
                Link 10 (tun0)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty),
            ResolvectlDnsResult = new ProcessResult(0, "Link 10 (tun0): 10.254.0.53\n", string.Empty),
            ResolvectlDomainResult = new ProcessResult(0, "Link 10 (tun0): ~.\n", string.Empty)
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.FileName == "ip"
            && command.Arguments.SequenceEqual(["-4", "route", "get", "10.254.0.53"]));
        Assert.Contains(runner.Commands, command => command.FileName == "ip"
            && command.Arguments.SequenceEqual(["-4", "route", "get", "1.1.1.1"]));
        Assert.Contains(runner.Commands, command => command.FileName == "ip"
            && command.Arguments.SequenceEqual(["-6", "route", "get", "2606:4700:4700::1111"]));
    }

    [Fact]
    public async Task ActivateAsync_WaitsForInstalledDispatcherBeforeRequestingElevation()
    {
        var runner = new RecordingRunner
        {
            IpRuleOutputs = ["220:\tfrom all lookup 220", string.Empty],
            DirectIpRuleDeleteResult = new ProcessResult(2, string.Empty, "RTNETLINK answers: Operation not permitted")
        };
        var client = new NetworkManagerClient(runner, _ => true, delay: (_, _) => Task.CompletedTask);

        await client.ActivateAsync(CreateIkeV2Profile(), CancellationToken.None);

        Assert.DoesNotContain(runner.Commands, command => command.FileName == "ip" && IsDeleteBrokenTable220Rule(command.Arguments));
        Assert.DoesNotContain(runner.Commands, command => command.FileName == "pkexec");
    }

    [Fact]
    public async Task ActivateAsync_VerifiesDispatcherBrowserDohCanary()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(
            runner,
            _ => true,
            diagnostics.Add,
            readAllText: path => path == "/etc/hosts"
                ? "127.0.0.1 localhost\n0.0.0.0 use-application-dns.net # LibreGuard VPN DoH canary\n"
                : throw new FileNotFoundException(path),
            verifyBrowserDohProtection: true);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.Contains(diagnostics, line =>
            line.Contains("vpn-browser-doh-verification", StringComparison.Ordinal)
            && line.Contains("status=active", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "down", "libreguard-openvpn"]));
    }

    [Fact]
    public async Task ActivateAsync_ClosesTunnelWhenDispatcherBrowserDohCanaryIsMissing()
    {
        var runner = new RecordingRunner();
        var client = new NetworkManagerClient(
            runner,
            _ => true,
            readAllText: _ => "127.0.0.1 localhost\n",
            verifyBrowserDohProtection: true);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("DNS-over-HTTPS canary", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "up", "libreguard-openvpn"]));
        Assert.Contains(runner.Commands, command =>
            command.Arguments.SequenceEqual(["connection", "down", "libreguard-openvpn"]));
    }

    [Fact]
    public async Task ActivateAsync_UsesTunnelDeviceWhenNetworkManagerReportsPhysicalParent()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = "enp0s3",
            ActiveDeviceStatusOutput = """
                enp0s3:ethernet:connected:Wired connection 1
                tun0:tun:connected:libreguard-openvpn
                """,
            ConnectionInterfaceNameOutput = "",
            ResolvectlStatusResult = new ProcessResult(0, """
                Link 2 (enp0s3)
                     DNS Servers: 192.0.2.53
                Link 10 (tun0)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty),
            ResolvectlDnsResult = new ProcessResult(0, "Link 10 (tun0): 10.254.0.53\n", string.Empty),
            ResolvectlDomainResult = new ProcessResult(0, "Link 10 (tun0): ~.\n", string.Empty),
            RouteOutputDevices = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["10.254.0.53"] = "tun0",
                ["1.1.1.1"] = "tun0",
                ["2606:4700:4700::1111"] = "tun0"
            }
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["dns", "tun0"]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["dns", "enp0s3"]));
        Assert.Equal("tun0", await client.GetActiveDeviceNameAsync("libreguard-openvpn", CancellationToken.None));
    }

    [Fact]
    public async Task ActivateAsync_RejectsPrivateDnsOwnedByPhysicalParentLink()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = "enp0s3",
            ActiveDeviceStatusOutput = """
                enp0s3:ethernet:connected:Wired connection 1
                tun0:tun:connected:libreguard-openvpn
                """,
            ConnectionInterfaceNameOutput = "",
            ResolvectlStatusResult = new ProcessResult(0, """
                Link 2 (enp0s3)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty)
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("physical interface", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["dns", "enp0s3"]));
    }

    [Fact]
    public async Task ActivateAsync_UsesOneAttemptForBaselineIpv4OnlyIkeV2Profile()
    {
        var runner = new RecordingRunner
        {
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: Unknown reason"),
            SecondConnectionUpResult = new ProcessResult(0, string.Empty, string.Empty),
            JournalResult = new ProcessResult(0, "charon: TS_UNACCEPTABLE for ::/0", string.Empty),
            FallbackVpnDataReadback = "address = 198.51.100.1, usercert = /tmp/client.crt, userkey = /tmp/client.key, certificate = /tmp/gateway.crt, method = key, remote-ts = 0.0.0.0/0",
            Ipv6RouteUnavailableAfterActivation = true
        };
        var client = new NetworkManagerClient(runner);

        await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateIkeV2Profile(), CancellationToken.None));

        var upCommands = runner.Commands
            .Where(command => command.Arguments.Count == 3
                && command.Arguments[0] == "connection"
                && command.Arguments[1] == "up")
            .ToArray();
        Assert.Single(upCommands);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("vpn.data")
            && command.Arguments[0] == "connection"
            && command.Arguments[1] == "modify");
    }

    [Fact]
    public async Task ActivateAsync_RetriesIkeV2WithAlternateGatewayRootWhenIssuerIsMissing()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"root-ye-{Guid.NewGuid():N}.crt").Replace('\\', '/');
        using var key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Root YE",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        await File.WriteAllTextAsync(rootPath, root.ExportCertificatePem());
        try
        {
            var runner = new RecordingRunner
            {
                ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: Unknown reason"),
                SecondConnectionUpResult = new ProcessResult(0, string.Empty, string.Empty),
                JournalResult = new ProcessResult(0, "charon: no issuer certificate found for \\\"C=US, O=Let's Encrypt, CN=YE2\\\"; issuer is \\\"C=US, O=ISRG, CN=Root YE\\\"", string.Empty),
                FallbackVpnDataReadback = $"address = 198.51.100.1, usercert = /tmp/client.crt, userkey = /tmp/client.key, certificate = {rootPath}, method = key, remote-ts = 0.0.0.0/0"
            };
            var settings = new InMemorySettingsStore();
            var client = new NetworkManagerClient(runner, _ => true, settingsStore: settings);
            var profile = CreateIkeV2Profile(["/tmp/gateway.crt", rootPath], allowPinnedRootFallback: true);

            await client.ActivateAsync(profile, CancellationToken.None);

            var upCommands = runner.Commands
                .Where(command => command.Arguments.Count == 3
                    && command.Arguments[0] == "connection"
                    && command.Arguments[1] == "up")
                .ToArray();
            Assert.Equal(2, upCommands.Length);
            var fallback = Assert.Single(runner.Commands, command => command.Arguments.Contains("vpn.data")
                && command.Arguments[0] == "connection"
                && command.Arguments[1] == "modify"
                && command.Arguments.Any(argument => argument.Contains(rootPath, StringComparison.Ordinal)));
            Assert.Contains(fallback.Arguments, argument => argument.Contains($"certificate={rootPath}", StringComparison.Ordinal));
            Assert.Equal(
                root.GetCertHashString(HashAlgorithmName.SHA256),
                await settings.GetAsync<string>(
                    IkeV2GatewayTrustPreference.SettingsKey(profile.ProfileName),
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(rootPath);
        }
    }

    [Fact]
    public async Task ActivateAsync_DoesNotSweepAlternateCertificatesForStandardGatewayCa()
    {
        var runner = new RecordingRunner
        {
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: Unknown reason"),
            SecondConnectionUpResult = new ProcessResult(0, string.Empty, string.Empty),
            JournalResult = new ProcessResult(0, "charon: no issuer certificate found", string.Empty)
        };
        var client = new NetworkManagerClient(runner);
        var profile = CreateIkeV2Profile(["/tmp/thundergrad-root-ca.crt", "/tmp/alternate.crt"]);

        await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(profile, CancellationToken.None));

        Assert.Single(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Any(argument => argument.Contains("alternate.crt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRetryIkeV2ForAuthenticationFailureAndRedactsDiagnostics()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner
        {
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: Unknown reason"),
            JournalResult = new ProcessResult(0, "charon: authentication failed userkey=/secret/private.key password=topsecret vpn.data=address=198.51.100.1,remote-ts=0.0.0.0/0;::/0", string.Empty)
        };
        var client = new NetworkManagerClient(runner, _ => true, diagnostics.Add);

        await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateIkeV2Profile(["/tmp/gateway.crt", "/tmp/root-ye.crt"]), CancellationToken.None));

        Assert.Single(runner.Commands, command => command.Arguments.Count == 3
            && command.Arguments[0] == "connection"
            && command.Arguments[1] == "up");
        var diagnosticText = string.Join('\n', diagnostics);
        Assert.DoesNotContain("topsecret", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/private.key", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-ts=0.0.0.0/0;::/0", diagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateAsync_RetriesPinnedRootsWhenJournalIsUnavailable()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner
        {
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: connect-failed"),
            SecondConnectionUpResult = new ProcessResult(0, string.Empty, string.Empty),
            JournalResult = new ProcessResult(1, string.Empty, "journalctl: permission denied")
        };
        var client = new NetworkManagerClient(runner, _ => true, diagnostics.Add);
        var profile = CreateIkeV2Profile(
            ["/tmp/root-x1.crt", "/tmp/root-x2.crt", "/tmp/root-ye.crt", "/tmp/root-yr.crt"],
            allowPinnedRootFallback: true);

        await client.ActivateAsync(profile, CancellationToken.None);

        Assert.Equal(2, runner.Commands.Count(command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName])));
        var diagnosticText = string.Join('\n', diagnostics);
        Assert.Contains("journal_available=false", diagnosticText, StringComparison.Ordinal);
        Assert.Contains("vpn-ikev2-gateway-certificate-attempt", diagnosticText, StringComparison.Ordinal);
        Assert.Contains("fingerprint=\"unknown\"", diagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRetryPinnedRootsForExplicitTransportFailure()
    {
        var runner = new RecordingRunner
        {
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: host unreachable"),
            JournalResult = new ProcessResult(1, string.Empty, "journalctl: permission denied")
        };
        var client = new NetworkManagerClient(runner, _ => true);
        var profile = CreateIkeV2Profile(
            ["/tmp/root-x1.crt", "/tmp/root-x2.crt", "/tmp/root-ye.crt", "/tmp/root-yr.crt"],
            allowPinnedRootFallback: true);

        await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ActivateAsync(profile, CancellationToken.None));

        Assert.Single(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
    }

    [Fact]
    public async Task ActivateAsync_ExhaustsPinnedRootsOnceAndReturnsSafeError()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner
        {
            ConnectionUpResult = new ProcessResult(4, string.Empty, "Connection activation failed: connect-failed"),
            JournalResult = new ProcessResult(1, string.Empty, "journalctl: permission denied")
        };
        var client = new NetworkManagerClient(runner, _ => true, diagnostics.Add);
        var profile = CreateIkeV2Profile(
            ["/tmp/root-x1.crt", "/tmp/root-x2.crt", "/tmp/root-ye.crt", "/tmp/root-yr.crt"],
            allowPinnedRootFallback: true);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ActivateAsync(profile, CancellationToken.None));

        Assert.Equal(4, runner.Commands.Count(command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName])));
        Assert.Contains("could not activate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vpn-ikev2-gateway-certificate-exhausted", string.Join('\n', diagnostics), StringComparison.Ordinal);
        Assert.DoesNotContain("password=", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_RefusesToContinueWhenPrivateDnsUsesPhysicalDevice()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = "tun0",
            RouteOutputDevice = "wlan0"
        };
        var client = new NetworkManagerClient(runner);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("private DNS resolver", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_RefusesToContinueWhenFullTunnelRouteUsesPhysicalDevice()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = "tun0",
            ResolvectlStatusResult = new ProcessResult(0, """
                Link 10 (tun0)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty),
            ResolvectlDnsResult = new ProcessResult(0, "Link 10 (tun0): 10.254.0.53\n", string.Empty),
            ResolvectlDomainResult = new ProcessResult(0, "Link 10 (tun0): ~.\n", string.Empty),
            RouteOutputDevices = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["1.1.1.1"] = "wlan0"
            }
        };
        var client = new NetworkManagerClient(runner);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("IPv4 full-tunnel traffic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_RefusesToContinueWhenActiveDnsIncludesPublicResolver()
    {
        var runner = new RecordingRunner
        {
            ActiveDnsOutput = "10.254.0.53\n1.1.1.1\n"
        };
        var client = new NetworkManagerClient(runner);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("active DNS configuration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_AllowsPhysicalDnsWhenPrivateVpnOwnsDefaultDnsRoute()
    {
        var runner = new RecordingRunner
        {
            ResolvectlStatusResult = new ProcessResult(0, """
                Link 2 (enp0s3)
                     DNS Servers: 192.0.2.53
                Link 10 (lgvpn0)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty)
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);
    }

    [Fact]
    public async Task ActivateAsync_RefusesToContinueWhenSystemResolverHasGlobalPublicDns()
    {
        var runner = new RecordingRunner
        {
            ResolvectlStatusResult = new ProcessResult(0, """
                Global
                     DNS Servers: 192.0.2.53
                Link 10 (lgvpn0)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty)
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("global non-private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_RefusesToContinueWhenPhysicalLinkCompetesForDefaultDnsRoute()
    {
        var runner = new RecordingRunner
        {
            ResolvectlStatusResult = new ProcessResult(0, """
                Link 2 (enp0s3)
                     DNS Servers: 192.0.2.53
                      DNS Domain: ~.
                Link 10 (lgvpn0)
                     DNS Servers: 10.254.0.53
                      DNS Domain: ~.
                """, string.Empty)
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("competing non-private default", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_AllowsHostsWithoutResolvectlAfterNetworkManagerDnsVerification()
    {
        var runner = new RecordingRunner
        {
            ResolvectlStatusResult = new ProcessResult(127, string.Empty, "resolvectl not found")
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.DoesNotContain(runner.Commands, command => command.FileName == "resolvectl" && command.Arguments.Contains("dns"));
    }

    [Fact]
    public async Task ActivateAsync_AllowsIpv4OnlyWhenIpv6HasNoPhysicalRoute()
    {
        var runner = new RecordingRunner
        {
            RouteResults = new Dictionary<string, ProcessResult>(StringComparer.Ordinal)
            {
                ["2606:4700:4700::1111"] = new(2, string.Empty, "Network is unreachable")
            }
        };
        var client = new NetworkManagerClient(runner);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", "libreguard-openvpn"]));
    }

    [Fact]
    public async Task ActivateAsync_AllowsContainedIpv4OnlyTunnelWhenDispatcherRejectsIpv6Lookup()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner
        {
            PostActivationIpv6RouteResult = new ProcessResult(
                2,
                string.Empty,
                "RTNETLINK answers: Operation not permitted")
        };
        var client = new NetworkManagerClient(runner, _ => true, diagnostics.Add);

        await client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None);

        Assert.Contains(diagnostics, line =>
            line.Contains("vpn-ipv6-fallback-contained", StringComparison.Ordinal)
            && line.Contains("exit_code=2", StringComparison.Ordinal)
            && line.Contains("Operation not permitted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivateAsync_RejectsIpv6VerificationWhenRouteCommandCannotStart()
    {
        var runner = new RecordingRunner
        {
            PostActivationIpv6RouteResult = new ProcessResult(
                127,
                string.Empty,
                "The requested process could not be started.")
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("could not verify safe IPv6", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActivateAsync_ContainsPhysicalIpv6ForIpv4OnlyTunnelAndRestoresItOnDisconnect()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(stateDirectory, "ipv6-leak-guard.json");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var runner = new RecordingRunner
            {
                HasPhysicalIpv6DefaultRoute = true,
                Ipv6RouteUnavailableAfterActivation = true
            };
            var client = new NetworkManagerClient(runner, _ => true, ipv6LeakGuardStatePath: statePath);
            var profile = CreateOpenVpnProfile();

            await client.ActivateAsync(profile, CancellationToken.None);

            var guardEnableIndex = runner.Commands.FindIndex(command => command.Arguments.SequenceEqual([
                "connection", "modify", runner.PhysicalConnectionName, "ipv6.never-default", "yes"]));
            var activationIndex = runner.Commands.FindIndex(command => command.Arguments.SequenceEqual(["connection", "up", profile.ProfileName]));
            Assert.True(guardEnableIndex >= 0);
            Assert.True(guardEnableIndex < activationIndex);
            Assert.Contains(runner.Commands, command => command.FileName == "ip"
                && command.Arguments.SequenceEqual(["-4", "route", "get", profile.OuterTransportAddress]));

            await client.DeactivateAsync(profile.ProfileName, CancellationToken.None);

            Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual([
                "connection", "modify", runner.PhysicalConnectionName, "ipv6.never-default", "no"]));
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ActivateAsync_RejectsPhysicalIpv6RouteAndRestoresTheGuard()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(stateDirectory, "ipv6-leak-guard.json");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var runner = new RecordingRunner
            {
                HasPhysicalIpv6DefaultRoute = true,
                RouteResults = new Dictionary<string, ProcessResult>(StringComparer.Ordinal)
                {
                    ["2606:4700:4700::1111"] = new(0, "2606:4700:4700::1111 dev enp0s3 src 2001:db8::2", string.Empty)
                }
            };
            var client = new NetworkManagerClient(runner, _ => true, ipv6LeakGuardStatePath: statePath);

            var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
                client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

            Assert.Contains("IPv6 full-tunnel", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "down", "libreguard-openvpn"]));
            Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual([
                "connection", "modify", runner.PhysicalConnectionName, "ipv6.never-default", "no"]));
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ActivateAsync_FailsBeforeConnectionUpWhenIpv6GuardCannotBeApplied()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(stateDirectory, "ipv6-leak-guard.json");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var runner = new RecordingRunner
            {
                HasPhysicalIpv6DefaultRoute = true,
                PhysicalIpv6GuardModifyResult = new ProcessResult(10, string.Empty, "permission denied")
            };
            var client = new NetworkManagerClient(runner, _ => true, ipv6LeakGuardStatePath: statePath);

            var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
                client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

            Assert.Contains("refused to update", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", "libreguard-openvpn"]));
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ActivateAsync_RejectsOuterTransportRouteThatUsesAnotherVpnDevice()
    {
        var runner = new RecordingRunner
        {
            PhysicalDeviceType = "vpn"
        };
        var client = new NetworkManagerClient(runner);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            client.ActivateAsync(CreateOpenVpnProfile(), CancellationToken.None));

        Assert.Contains("outer IPv4 transport route", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "up", "libreguard-openvpn"]));
    }

    [Fact]
    public async Task EnsureAvailableAsync_RestoresStaleIpv6GuardStateBeforeDeletingProfiles()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(stateDirectory, "ipv6-leak-guard.json");
        Directory.CreateDirectory(stateDirectory);
        await File.WriteAllTextAsync(statePath, """
            {"profileName":"libreguard-openvpn","connectionName":"Wired connection 1","connectionUuid":"11111111-2222-3333-4444-555555555555","deviceName":"enp0s3","originalNeverDefault":false}
            """);
        try
        {
            var runner = new RecordingRunner
            {
                PhysicalIpv6NeverDefault = "yes"
            };
            var client = new NetworkManagerClient(runner, _ => true, ipv6LeakGuardStatePath: statePath);

            await client.EnsureAvailableAsync(CancellationToken.None);

            Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual([
                "connection", "modify", runner.PhysicalConnectionName, "ipv6.never-default", "no"]));
            Assert.False(File.Exists(statePath));
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ActivateAsync_FailsWithSetupMessage_WhenHelperIsMissing()
    {
        var runner = new RecordingRunner
        {
            IpRuleOutput = "220:\tfrom all lookup 220",
            DirectIpRuleDeleteResult = new ProcessResult(2, string.Empty, "RTNETLINK answers: Operation not permitted")
        };
        var client = new NetworkManagerClient(runner, _ => false);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => client.ActivateAsync(CreateIkeV2Profile(), CancellationToken.None));

        Assert.Contains("install-linux-privileges.sh", ex.Message);
        Assert.Contains(RouteRepairHelperPath, ex.Message);
        Assert.DoesNotContain(runner.Commands, command => command.FileName == "pkexec");
    }

    [Fact]
    public async Task GetActiveDeviceNameAsync_ReturnsPrimaryDevice()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = "lgvpn0\n"
        };
        var client = new NetworkManagerClient(runner);

        var device = await client.GetActiveDeviceNameAsync("libreguard-ike", CancellationToken.None);

        Assert.Equal("lgvpn0", device);
        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["-g", "GENERAL.DEVICES", "connection", "show", "libreguard-ike"]));
    }

    [Fact]
    public async Task GetActiveDeviceNameAsync_UsesActiveAddressRouteProofForExternallyAssumedOpenVpnTun()
    {
        var diagnostics = new List<string>();
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = string.Empty,
            ConnectionInterfaceNameOutput = string.Empty,
            ActiveDeviceStatusOutput = "tun0:tun:connected (externally):tun0",
            ActiveIpv4AddressOutput = "10.151.224.7/19",
            VpnInitiallyActivated = true,
            RouteResults = new Dictionary<string, ProcessResult>(StringComparer.Ordinal)
            {
                ["1.1.1.1"] = new(0, "1.1.1.1 via 10.151.224.1 dev tun0 src 10.151.224.7", string.Empty)
            }
        };
        var client = new NetworkManagerClient(runner, _ => true, diagnostics.Add);

        var device = await client.GetActiveDeviceNameAsync(
            "libreguard-openvpn-de-multi-1-3",
            CancellationToken.None);

        Assert.Equal("tun0", device);
        Assert.Contains(diagnostics, line =>
            line.Contains("vpn-device-discovery-fallback", StringComparison.Ordinal)
            && line.Contains("proof=active-profile-address-and-default-route", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetActiveDeviceNameAsync_RejectsExternallyAssumedTunWhenRouteSourceIsNotOwnedByProfile()
    {
        var runner = new RecordingRunner
        {
            ActiveDeviceOutput = string.Empty,
            ConnectionInterfaceNameOutput = string.Empty,
            ActiveDeviceStatusOutput = "tun0:tun:connected (externally):tun0",
            ActiveIpv4AddressOutput = "10.151.224.7/19",
            VpnInitiallyActivated = true,
            RouteResults = new Dictionary<string, ProcessResult>(StringComparer.Ordinal)
            {
                ["1.1.1.1"] = new(0, "1.1.1.1 via 192.0.2.1 dev tun0 src 192.0.2.10", string.Empty)
            }
        };
        var client = new NetworkManagerClient(runner);

        var device = await client.GetActiveDeviceNameAsync(
            "libreguard-openvpn-de-multi-1-3",
            CancellationToken.None);

        Assert.Null(device);
    }

    [Fact]
    public async Task GetActiveLibreGuardProfilesAsync_ReturnsOnlyActiveLibreGuardVpnProfiles()
    {
        var runner = new RecordingRunner
        {
            ActiveConnectionsOutput = """
                libreguard-openvpn-nl-1:vpn
                libreguard-ikev2-ch-2:vpn
                libreguard-ikev2-ch-2:wireguard
                corp-vpn:vpn
                Wired connection 1:802-3-ethernet
                """
        };
        var client = new NetworkManagerClient(runner);

        var profiles = await client.GetActiveLibreGuardProfilesAsync(CancellationToken.None);

        Assert.Equal(["libreguard-openvpn-nl-1", "libreguard-ikev2-ch-2"], profiles);
    }

    [Fact]
    public async Task DisconnectLibreGuardProfilesAsync_DeactivatesOnlyActiveLibreGuardProfiles()
    {
        var runner = new RecordingRunner
        {
            ActiveConnectionsOutput = """
                libreguard-openvpn-nl-1:vpn
                libreguard-ikev2-ch-2:vpn
                corp-vpn:vpn
                """
        };
        var client = new NetworkManagerClient(runner);

        await client.DisconnectLibreGuardProfilesAsync(CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "down", "libreguard-openvpn-nl-1"]));
        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "down", "libreguard-ikev2-ch-2"]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "down", "corp-vpn"]));
    }

    [Fact]
    public async Task DeleteLibreGuardProfilesAsync_DeletesOnlyLibreGuardProfilesExceptExcludedOne()
    {
        var runner = new RecordingRunner
        {
            ConnectionsOutput = """
                libreguard-openvpn-nl-1:vpn
                libreguard-ikev2-ch-2:vpn
                corp-vpn:vpn
                Wired connection 1:802-3-ethernet
                """
        };
        var client = new NetworkManagerClient(runner);

        await client.DeleteLibreGuardProfilesAsync("libreguard-ikev2-ch-2", CancellationToken.None);

        Assert.Contains(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "delete", "libreguard-openvpn-nl-1"]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "delete", "libreguard-ikev2-ch-2"]));
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.SequenceEqual(["connection", "delete", "corp-vpn"]));
    }

    [Fact]
    public async Task CleanupLibreGuardArtifactsAsync_CleansCurrentAndLegacyDirectories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var currentDirectory = Path.Combine(tempRoot, "credentials");
        var stateHome = Path.Combine(tempRoot, "state");
        var legacyDirectory = Path.Combine(stateHome, "libreguard", "configs");
        var previousCredentialDirectory = Environment.GetEnvironmentVariable(
            XdgPaths.VpnCredentialDirectoryEnvironmentVariable);
        var previousStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");

        try
        {
            Environment.SetEnvironmentVariable(
                XdgPaths.VpnCredentialDirectoryEnvironmentVariable,
                currentDirectory);
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", stateHome);
            Directory.CreateDirectory(currentDirectory);
            Directory.CreateDirectory(legacyDirectory);

            var currentOwnedFile = Path.Combine(currentDirectory, "libreguard-openvpn-nl-1.ovpn");
            var legacyOwnedFile = Path.Combine(legacyDirectory, "libreguard-ikev2-de-2.key");
            var excludedFile = Path.Combine(currentDirectory, "libreguard-openvpn-us-3.askpass");
            var unrelatedFile = Path.Combine(legacyDirectory, "personal-vpn.ovpn");
            await File.WriteAllTextAsync(currentOwnedFile, "owned");
            await File.WriteAllTextAsync(legacyOwnedFile, "owned");
            await File.WriteAllTextAsync(excludedFile, "excluded");
            await File.WriteAllTextAsync(unrelatedFile, "unrelated");

            var client = new NetworkManagerClient(new RecordingRunner());

            await client.CleanupLibreGuardArtifactsAsync(
                "libreguard-openvpn-us-3",
                CancellationToken.None);

            Assert.False(File.Exists(currentOwnedFile));
            Assert.False(File.Exists(legacyOwnedFile));
            Assert.True(File.Exists(excludedFile));
            Assert.True(File.Exists(unrelatedFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                XdgPaths.VpnCredentialDirectoryEnvironmentVariable,
                previousCredentialDirectory);
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", previousStateHome);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<CommandRecord> Commands { get; } = [];
        public string NmcliVersionOutput { get; init; } = "nmcli tool, version 1.52.0";
        public string IpRuleOutput { get; init; } = string.Empty;
        public IReadOnlyList<string>? IpRuleOutputs { get; init; }
        public ProcessResult DirectIpRuleDeleteResult { get; init; } = new(0, string.Empty, string.Empty);
        public ProcessResult ElevatedRouteRepairResult { get; init; } = new(0, string.Empty, string.Empty);
        public string ActiveDeviceOutput { get; init; } = "lgvpn0";
        public string? ActiveDeviceStatusOutput { get; init; }
        public string? ConnectionInterfaceNameOutput { get; init; }
        public string? RouteOutputDevice { get; init; }
        public IReadOnlyDictionary<string, string>? RouteOutputDevices { get; init; }
        public IReadOnlyDictionary<string, ProcessResult>? RouteResults { get; init; }
        public string ActiveDnsOutput { get; init; } = "10.254.0.53\n";
        public string ActiveIpv4AddressOutput { get; init; } = "10.8.0.2/24\n";
        public ProcessResult ResolvectlStatusResult { get; init; } = new(0, """
            Link 10 (lgvpn0)
                 DNS Servers: 10.254.0.53
                  DNS Domain: ~.
            """, string.Empty);
        public ProcessResult ResolvectlDnsResult { get; init; } = new(0, "Link 10 (lgvpn0): 10.254.0.53\n", string.Empty);
        public ProcessResult ResolvectlDomainResult { get; init; } = new(0, "Link 10 (lgvpn0): ~.\n", string.Empty);
        public string ActiveConnectionsOutput { get; init; } = string.Empty;
        public string ConnectionsOutput { get; init; } = string.Empty;
        public string OpenVpnImportOutput { get; init; } = "ok";
        public ProcessResult PrivateDnsModifyResult { get; init; } = new(0, string.Empty, string.Empty);
        public string? PrivateDnsQueryFailureProperty { get; init; }
        public string? PrivateDnsMismatchProperty { get; init; }
        public IReadOnlySet<string>? EmptyConfiguredProperties { get; init; }
        public IReadOnlyDictionary<string, string>? ConfiguredSettingOverrides { get; init; }
        public string VpnDataReadback { get; init; } = "address = 198.51.100.1, usercert = /tmp/client.crt, userkey = /tmp/client.key, certificate = /tmp/gateway.crt, method = key, remote-ts = 0.0.0.0/0";
        public string PhysicalDeviceName { get; init; } = "enp0s3";
        public string PhysicalConnectionName { get; init; } = "Wired connection 1";
        public string PhysicalConnectionUuid { get; init; } = "11111111-2222-3333-4444-555555555555";
        public string PhysicalDeviceType { get; init; } = "ethernet";
        public string PhysicalIpv6NeverDefault { get; init; } = "no";
        public bool HasPhysicalIpv6DefaultRoute { get; init; }
        public bool Ipv6RouteUnavailableAfterActivation { get; init; }
        public ProcessResult? PostActivationIpv6RouteResult { get; init; }
        public ProcessResult PhysicalIpv6GuardModifyResult { get; init; } = new(0, string.Empty, string.Empty);
        public ProcessResult PhysicalIpv6GuardReapplyResult { get; init; } = new(0, string.Empty, string.Empty);
        public ProcessResult ConnectionUpResult { get; init; } = new(0, string.Empty, string.Empty);
        public bool VpnInitiallyActivated { get; init; }
        public ProcessResult? SecondConnectionUpResult { get; init; }
        public string ProfileUuidOutput { get; init; } = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        public string ActivationStateOutput { get; init; } = "20 (unavailable)\nunknown\n";
        public ProcessResult JournalResult { get; init; } = new(0, string.Empty, string.Empty);
        public string? FallbackVpnDataReadback { get; init; }
        public string? SetfaclGrantFailurePath { get; init; }

        private bool _vpnActivated;
        private bool? _physicalIpv6NeverDefault;
        private bool _fallbackConfigured;
        private int _connectionUpAttempts;
        private int _ipRuleQueryCount;
        private string? _lastFallbackVpnData;

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            Commands.Add(new CommandRecord(fileName, args));

            if (fileName == "nmcli" && args.SequenceEqual(["--version"]))
            {
                return Task.FromResult(new ProcessResult(0, NmcliVersionOutput, string.Empty));
            }

            if (fileName == "journalctl")
            {
                return Task.FromResult(JournalResult);
            }

            if (fileName == "getfacl")
            {
                var target = args[^1];
                var ownerPermissions = Path.HasExtension(target) ? "rw-" : "rwx";
                return Task.FromResult(new ProcessResult(
                    0,
                    $"user::{ownerPermissions}\ngroup::---\nother::---\n",
                    string.Empty));
            }

            if (fileName == "setfacl"
                && args.Contains("--modify")
                && string.Equals(args[^1], SetfaclGrantFailurePath, StringComparison.Ordinal))
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "simulated ACL grant failure"));
            }

            if (fileName == "ip" && args is ["rule", "show"])
            {
                var output = IpRuleOutputs is { Count: > 0 }
                    ? IpRuleOutputs[Math.Min(_ipRuleQueryCount++, IpRuleOutputs.Count - 1)]
                    : IpRuleOutput;
                return Task.FromResult(new ProcessResult(0, output, string.Empty));
            }

            if (fileName == "ip" && IsDeleteBrokenTable220Rule(args))
            {
                return Task.FromResult(DirectIpRuleDeleteResult);
            }

            if (fileName == "pkexec" && args.SequenceEqual([RouteRepairHelperPath]))
            {
                return Task.FromResult(ElevatedRouteRepairResult);
            }

            if (fileName == "ip"
                && args.Length == 4
                && (args[0] == "-4" || args[0] == "-6")
                && args[1] == "route"
                && args[2] == "get")
            {
                if (!_vpnActivated && !VpnInitiallyActivated)
                {
                    if (args[0] == "-6")
                    {
                        return Task.FromResult(HasPhysicalIpv6DefaultRoute && _physicalIpv6NeverDefault != true
                            ? new ProcessResult(0, $"{args[3]} dev {PhysicalDeviceName} src 2001:db8::2", string.Empty)
                            : new ProcessResult(2, string.Empty, "Network is unreachable"));
                    }

                    return Task.FromResult(new ProcessResult(0, $"{args[3]} dev {PhysicalDeviceName} src 192.0.2.10", string.Empty));
                }

                if (RouteResults is not null && RouteResults.TryGetValue(args[3], out var routeResult))
                {
                    return Task.FromResult(routeResult);
                }

                if (args[0] == "-6" && PostActivationIpv6RouteResult is not null)
                {
                    return Task.FromResult(PostActivationIpv6RouteResult);
                }

                if (args[0] == "-6" && Ipv6RouteUnavailableAfterActivation)
                {
                    return Task.FromResult(new ProcessResult(2, string.Empty, "Network is unreachable"));
                }

                var routeDevice = RouteOutputDevices is not null
                    && RouteOutputDevices.TryGetValue(args[3], out var targetDevice)
                    ? targetDevice
                    : RouteOutputDevice ?? ActiveDeviceOutput;
                return Task.FromResult(new ProcessResult(0, $"{args[3]} dev {routeDevice} src 10.8.0.2", string.Empty));
            }

            if (fileName == "resolvectl" && args.SequenceEqual(["status"]))
            {
                return Task.FromResult(ResolvectlStatusResult);
            }

            if (fileName == "resolvectl" && args.Length == 2 && args[0] == "dns")
            {
                return Task.FromResult(ResolvectlDnsResult);
            }

            if (fileName == "resolvectl" && args.Length == 2 && args[0] == "domain")
            {
                return Task.FromResult(ResolvectlDomainResult);
            }

            if (args.Contains("import") && args.Contains("openvpn"))
            {
                return Task.FromResult(new ProcessResult(0, OpenVpnImportOutput, string.Empty));
            }

            if (args.SequenceEqual(["-g", "GENERAL.CONNECTION", "device", "show", PhysicalDeviceName]))
            {
                return Task.FromResult(new ProcessResult(0, PhysicalConnectionName, string.Empty));
            }

            if (args.SequenceEqual(["-g", "GENERAL.TYPE", "device", "show", PhysicalDeviceName]))
            {
                return Task.FromResult(new ProcessResult(0, PhysicalDeviceType, string.Empty));
            }

            if (args.Length == 5
                && args[0] == "-g"
                && args[1] == "GENERAL.TYPE"
                && args[2] == "device"
                && args[3] == "show")
            {
                return Task.FromResult(new ProcessResult(0, "tun", string.Empty));
            }

            if (args.SequenceEqual(["-g", "connection.uuid", "connection", "show", PhysicalConnectionName]))
            {
                return Task.FromResult(new ProcessResult(0, PhysicalConnectionUuid, string.Empty));
            }

            if (args.SequenceEqual(["-g", "ipv6.never-default", "connection", "show", PhysicalConnectionName]))
            {
                var value = _physicalIpv6NeverDefault ?? string.Equals(PhysicalIpv6NeverDefault, "yes", StringComparison.OrdinalIgnoreCase);
                return Task.FromResult(new ProcessResult(0, value ? "yes" : "no", string.Empty));
            }

            if (args.SequenceEqual(["connection", "modify", PhysicalConnectionName, "ipv6.never-default", "yes"]))
            {
                if (PhysicalIpv6GuardModifyResult.Success)
                {
                    _physicalIpv6NeverDefault = true;
                }

                return Task.FromResult(PhysicalIpv6GuardModifyResult);
            }

            if (args.SequenceEqual(["connection", "modify", PhysicalConnectionName, "ipv6.never-default", "no"]))
            {
                if (PhysicalIpv6GuardModifyResult.Success)
                {
                    _physicalIpv6NeverDefault = false;
                }

                return Task.FromResult(PhysicalIpv6GuardModifyResult);
            }

            if (args.SequenceEqual(["device", "reapply", PhysicalDeviceName]))
            {
                return Task.FromResult(PhysicalIpv6GuardReapplyResult);
            }

            if (args.Length == 5
                && args[0] == "-g"
                && args[1] == "vpn.data"
                && args[2] == "connection"
                && args[3] == "show")
            {
                return Task.FromResult(new ProcessResult(0, _fallbackConfigured
                    ? FallbackVpnDataReadback ?? _lastFallbackVpnData ?? VpnDataReadback
                    : VpnDataReadback, string.Empty));
            }

            if (args.SequenceEqual(["-g", "connection.uuid", "connection", "show", "libreguard-openvpn"])
                || (args.Length == 5 && args[0] == "-g" && args[1] == "connection.uuid" && args[2] == "connection" && args[3] == "show"))
            {
                return Task.FromResult(new ProcessResult(0, ProfileUuidOutput, string.Empty));
            }

            if (args.Length == 5
                && args[0] == "-g"
                && args[1] == "connection.interface-name"
                && args[2] == "connection"
                && args[3] == "show")
            {
                return Task.FromResult(new ProcessResult(0, ConnectionInterfaceNameOutput ?? ActiveDeviceOutput, string.Empty));
            }

            if (args.SequenceEqual(["-g", "GENERAL.STATE,GENERAL.REASON,GENERAL.DEVICES", "connection", "show", "libreguard-openvpn"])
                || (args.Length == 5 && args[0] == "-g" && args[1] == "GENERAL.STATE,GENERAL.REASON,GENERAL.DEVICES" && args[2] == "connection" && args[3] == "show"))
            {
                return Task.FromResult(new ProcessResult(0, ActivationStateOutput + ActiveDeviceOutput + "\n", string.Empty));
            }

            if (args.Length == 3
                && args[0] == "connection"
                && args[1] == "up")
            {
                var result = _connectionUpAttempts++ == 0
                    ? ConnectionUpResult
                    : SecondConnectionUpResult ?? ConnectionUpResult;
                _vpnActivated = result.Success;
                return Task.FromResult(result);
            }

            if (args.Length == 3
                && args[0] == "connection"
                && args[1] == "down")
            {
                _vpnActivated = false;
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            if (IsPrivateDnsModify(args))
            {
                return Task.FromResult(PrivateDnsModifyResult);
            }

            if (args.Length >= 5
                && args[0] == "connection"
                && args[1] == "modify"
                && args.Contains("vpn.data"))
            {
                _fallbackConfigured = true;
                var vpnDataIndex = Array.IndexOf(args, "vpn.data");
                if (vpnDataIndex >= 0 && vpnDataIndex < args.Length - 1)
                {
                    _lastFallbackVpnData = args[vpnDataIndex + 1];
                }
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            if (TryGetConfiguredSettingQueryProperty(args, out var dnsProperty))
            {
                if (string.Equals(dnsProperty, PrivateDnsQueryFailureProperty, StringComparison.Ordinal))
                {
                    return Task.FromResult(new ProcessResult(10, string.Empty, "query failed"));
                }

                var fullTunnelSettings = args[4].Contains("ike", StringComparison.OrdinalIgnoreCase)
                    ? ExpectedIkeV2FullTunnelSettings
                    : ExpectedFullTunnelSettings;
                var storedValue = ExpectedPrivateDnsSettings
                    .Concat(ExpectedLegacyPrivateDnsSettings)
                    .Concat(fullTunnelSettings)
                    .Single(setting => setting.Name == dnsProperty)
                    .Value;
                if (EmptyConfiguredProperties?.Contains(dnsProperty) == true)
                {
                    storedValue = "--";
                }
                if (string.Equals(dnsProperty, PrivateDnsMismatchProperty, StringComparison.Ordinal))
                {
                    storedValue = "unexpected";
                }
                if (ConfiguredSettingOverrides is not null
                    && ConfiguredSettingOverrides.TryGetValue(dnsProperty, out var overrideValue))
                {
                    storedValue = overrideValue;
                }

                return Task.FromResult(new ProcessResult(0, storedValue, string.Empty));
            }

            if (args.Length == 7
                && args[0] == "-g"
                && args[1] == "IP4.ADDRESS"
                && args[2] == "connection"
                && args[3] == "show"
                && args[4] == "--active"
                && args[5] == "id")
            {
                return Task.FromResult(new ProcessResult(0, ActiveIpv4AddressOutput, string.Empty));
            }

            if (args.Length == 7
                && args[0] == "-g"
                && args[1] == "IP4.DNS,IP6.DNS"
                && args[2] == "connection"
                && args[3] == "show"
                && args[4] == "--active"
                && args[5] == "id")
            {
                return Task.FromResult(new ProcessResult(0, ActiveDnsOutput, string.Empty));
            }

            if (args.Length == 5
                && args[0] == "-g"
                && args[1] == "GENERAL.DEVICES"
                && args[2] == "connection"
                && args[3] == "show")
            {
                return Task.FromResult(new ProcessResult(0, ActiveDeviceOutput, string.Empty));
            }

            if (args.SequenceEqual(["-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"]))
            {
                var output = ActiveDeviceStatusOutput
                    ?? $"{ActiveDeviceOutput}:tun:connected:libreguard-openvpn";
                return Task.FromResult(new ProcessResult(0, output, string.Empty));
            }

             if (args.SequenceEqual(["-t", "-f", "NAME,TYPE", "connection", "show", "--active"]))
            {
                return Task.FromResult(new ProcessResult(0, ActiveConnectionsOutput, string.Empty));
            }

            if (args.SequenceEqual(["-t", "-f", "NAME,TYPE", "connection", "show"]))
            {
                return Task.FromResult(new ProcessResult(0, ConnectionsOutput, string.Empty));
            }

            return Task.FromResult(new ProcessResult(0, "ok", string.Empty));
        }
    }

    private static bool HasOption(IReadOnlyList<string> arguments, string name, string value)
    {
        var index = arguments.ToList().IndexOf(name);
        return index >= 0 && index < arguments.Count - 1 && arguments[index + 1] == value;
    }

    private static bool IsDeleteBrokenTable220Rule(IReadOnlyList<string> arguments)
        => arguments.SequenceEqual(["rule", "del", "pref", "220", "from", "all", "lookup", "220"]);

    private static int AssertPrivateDnsConfiguredAndVerified(RecordingRunner runner, string profileName)
    {
        var modifyIndex = runner.Commands.FindIndex(command =>
            command.Arguments.Count >= 3
            && command.Arguments[0] == "connection"
            && command.Arguments[1] == "modify"
            && HasOption(command.Arguments, "connection.id", profileName)
            && ExpectedPrivateDnsSettings.All(setting => HasOption(command.Arguments, setting.Name, setting.Value)));
        Assert.True(modifyIndex >= 0);
        foreach (var (name, _) in ExpectedPrivateDnsSettings)
        {
            var queryIndex = runner.Commands.FindIndex(command => command.Arguments.SequenceEqual(["-g", name, "connection", "show", profileName]));
            Assert.True(queryIndex > modifyIndex);
        }

        return modifyIndex;
    }

    private static void AssertFullTunnelConfiguredAndVerified(
        RecordingRunner runner,
        string profileName,
        IReadOnlyList<(string Name, string Value)>? expectedSettings = null)
    {
        expectedSettings ??= ExpectedFullTunnelSettings;
        var modifyIndex = runner.Commands.FindIndex(command =>
            command.Arguments.Count >= 3
            && command.Arguments[0] == "connection"
            && command.Arguments[1] == "modify"
            && HasOption(command.Arguments, "connection.id", profileName)
            && expectedSettings.All(setting => HasOption(command.Arguments, setting.Name, setting.Value)));
        Assert.True(modifyIndex >= 0);
        foreach (var (name, _) in expectedSettings)
        {
            var queryIndex = runner.Commands.FindIndex(command => command.Arguments.SequenceEqual(["-g", name, "connection", "show", profileName]));
            Assert.True(queryIndex > modifyIndex);
        }
    }

    private static bool IsPrivateDnsModify(IReadOnlyList<string> arguments)
        => arguments.Count >= 3
            && arguments[0] == "connection"
            && arguments[1] == "modify"
            && arguments.Contains("ipv4.dns");

    private static bool IsPrivateDnsQuery(IReadOnlyList<string> arguments)
        => TryGetConfiguredSettingQueryProperty(arguments, out _)
            && ExpectedPrivateDnsSettings.Any(setting => setting.Name == arguments[1]);

    private static bool TryGetConfiguredSettingQueryProperty(IReadOnlyList<string> arguments, out string propertyName)
    {
        var candidatePropertyName = arguments.Count > 1 ? arguments[1] : string.Empty;
        propertyName = candidatePropertyName;
        return arguments.Count == 5
            && arguments[0] == "-g"
            && arguments[2] == "connection"
            && arguments[3] == "show"
            && ExpectedPrivateDnsSettings
                .Concat(ExpectedLegacyPrivateDnsSettings)
                .Concat(ExpectedFullTunnelSettings)
                .Concat(ExpectedIkeV2FullTunnelSettings)
                .Any(setting => setting.Name == candidatePropertyName);
    }

    private static VpnProfile CreateOpenVpnProfile(string profileName = "libreguard-openvpn")
        => new(
            VpnProtocol.OpenVpn,
            profileName,
            $"/tmp/{profileName}.ovpn",
            null,
            null,
            "198.51.100.1");

    private static VpnProfile CreateIkeV2Profile(
        IReadOnlyList<string>? gatewayCertificatePaths = null,
        bool allowPinnedRootFallback = false,
        string remoteAddress = "198.51.100.1",
        IReadOnlyList<string>? credentialPaths = null)
    {
        var effectiveCredentialPaths = credentialPaths
            ?? ["/tmp/client.crt", "/tmp/client.key", "/tmp/gateway.crt", .. (gatewayCertificatePaths ?? [])];

        return new(
            VpnProtocol.Ikev2,
            "libreguard-ike",
            "/tmp/libreguard-ike.sswan",
            null,
            $"address={remoteAddress},usercert=/tmp/client.crt,userkey=/tmp/client.key,certificate=/tmp/gateway.crt,method=key,virtual=yes,remote-ts=0.0.0.0/0",
            "198.51.100.1",
            gatewayCertificatePaths,
            allowPinnedRootFallback,
            remoteAddress,
            effectiveCredentialPaths);
    }

    private sealed record CommandRecord(string FileName, IReadOnlyList<string> Arguments);
}
