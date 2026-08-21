using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class OpenVpnProfileConverterTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string? _previousXdgStateHome;
    private readonly string? _previousXdgConfigHome;
    private readonly string? _previousVpnCredentialDirectory;

    public OpenVpnProfileConverterTests()
    {
        _previousXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        _previousXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _previousVpnCredentialDirectory = Environment.GetEnvironmentVariable(XdgPaths.VpnCredentialDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(_tempRoot, "state"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_tempRoot, "config"));
        Environment.SetEnvironmentVariable(
            XdgPaths.VpnCredentialDirectoryEnvironmentVariable,
            Path.Combine(_tempRoot, "credentials"));
    }

    [Fact]
    public async Task ConvertAsync_WritesAllowedConfigAndAskpassFile()
    {
        var converter = new OpenVpnProfileConverter(new FakeDeviceIdentityService("secret-pass"));
        var server = new VpnServer(7, "Test NL", "10.0.0.1", "nl.example", "Netherlands", "Amsterdam", 100, "free", 20, 2, 443, true);
        var config = new VpnConfigResponse(
            true,
            "OpenVPN",
            "Test NL",
            "10.0.0.1",
            "cert",
            """
            client
            dev tun
            proto udp
            remote nl.example 1194
            remote-cert-tls server
            cipher AES-256-GCM
            auth SHA256
            resolv-retry infinite
            nobind
            persist-key
            persist-tun
            verb 3
            <ca>
            -----BEGIN CERTIFICATE-----
            test-ca
            -----END CERTIFICATE-----
            </ca>
            askpass [ENCRYPTED_PASSPHRASE]

            """,
            new EncryptedPassphrase("RSA-OAEP-256", "key", "cipher"),
            "10.8.0.2",
            "device",
            null);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var output = await File.ReadAllTextAsync(profile.ConfigPath);
        Assert.Contains("remote nl.example 1194", output);
        Assert.DoesNotContain("remote 10.0.0.1", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("10.0.0.1", profile.OuterTransportAddress);
        Assert.Contains("<ca>", output);
        Assert.Contains("</ca>", output);
        Assert.Contains(profile.SecretPath!, output);
        Assert.Equal("secret-pass", await File.ReadAllTextAsync(profile.SecretPath!));
        Assert.Equal(XdgPaths.VpnCredentialDirectory, Path.GetDirectoryName(profile.ConfigPath));
        AssertPrivatePermissions(profile.ConfigPath, isDirectory: false);
        AssertPrivatePermissions(profile.SecretPath!, isDirectory: false);
        AssertPrivatePermissions(XdgPaths.VpnCredentialDirectory, isDirectory: true);
    }

    [Fact]
    public async Task ConvertAsync_AppendsAskpassWhenBackendPlaceholderIsMissing()
    {
        var converter = new OpenVpnProfileConverter(new FakeDeviceIdentityService("secret-pass"));
        var server = new VpnServer(8, "Test DE", "10.0.0.2", "de.example", "Germany", "Frankfurt", 100, "free", 20, 2, 443, true);
        var config = new VpnConfigResponse(
            true,
            "OpenVPN",
            "Test DE",
            "10.0.0.2",
            "cert",
            "client\nremote de.example 1194\n",
            new EncryptedPassphrase("RSA-OAEP-256", "key", "cipher"),
            "10.8.0.3",
            "device",
            null);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var output = await File.ReadAllTextAsync(profile.ConfigPath);
        Assert.Contains($"askpass {profile.SecretPath}", output);
    }

    [Fact]
    public async Task ConvertAsync_PreservesBackendEndpointAndRoutesWhileNormalizingPrivateDns()
    {
        var converter = new OpenVpnProfileConverter(new FakeDeviceIdentityService("secret-pass"));
        var server = new VpnServer(12, "Test Full Tunnel", "10.0.0.12", "full.example", "Germany", "Berlin", 100, "free", 20, 2, 443, true);
        var config = new VpnConfigResponse(
            true,
            "OpenVPN",
            "Test Full Tunnel",
            "10.0.0.12",
            "cert",
            """
            client
            remote full.example 1194
            route-nopull
            redirect-private
            route 10.20.0.0 255.255.0.0
            route-ipv6 2001:db8::/32
            dhcp-option DNS 8.8.8.8
            pull-filter accept "dhcp-option DNS"
            """,
            null,
            "10.8.0.12",
            "device",
            null);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var output = await File.ReadAllTextAsync(profile.ConfigPath);
        Assert.Contains("remote full.example 1194", output);
        Assert.Contains("pull-filter ignore \"dhcp-option DNS\"", output);
        Assert.Contains("pull-filter ignore \"dhcp-option DNS6\"", output);
        Assert.Contains("dhcp-option DNS 10.254.0.53", output);
        Assert.Contains("route-nopull", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redirect-private", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("route 10.20.0.0", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("route-ipv6 2001:db8::/32", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("redirect-gateway def1 ipv6", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dhcp-option DNS 8.8.8.8", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull-filter accept \"dhcp-option DNS\"", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertAsync_RejectsProfileWithoutLiteralIpv4OuterTransportAddress()
    {
        var converter = new OpenVpnProfileConverter(new FakeDeviceIdentityService("secret-pass"));
        var server = new VpnServer(18, "No IPv4", "vpn.example", "vpn.example", "Germany", "Berlin", 100, "free", 20, 2, 443, true);
        var config = new VpnConfigResponse(
            true,
            "OpenVPN",
            "No IPv4",
            "vpn.example",
            "cert",
            "client\nremote vpn.example 1194\n",
            null,
            "10.8.0.18",
            "device",
            null);

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            converter.ConvertAsync(config, server, CancellationToken.None));

        Assert.Contains("literal IPv4", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("script-security 2")]
    [InlineData("up /tmp/run")]
    [InlineData("down /tmp/run")]
    [InlineData("route-up /tmp/run")]
    [InlineData("route-pre-down /tmp/run")]
    [InlineData("ipchange /tmp/run")]
    [InlineData("tls-verify /tmp/run")]
    [InlineData("auth-user-pass-verify /tmp/run via-file")]
    [InlineData("learn-address /tmp/run")]
    [InlineData("plugin /tmp/openvpn-plugin.so")]
    [InlineData("management 127.0.0.1 7505")]
    public async Task ConvertAsync_RejectsExecutableOpenVpnDirective(string directive)
    {
        var converter = new OpenVpnProfileConverter(new FakeDeviceIdentityService("secret-pass"));
        var server = new VpnServer(9, "Test FR", "10.0.0.3", "fr.example", "France", "Paris", 100, "free", 20, 2, 443, true);
        var config = new VpnConfigResponse(
            true,
            "OpenVPN",
            "Test FR",
            "10.0.0.3",
            "cert",
            $"client\nremote fr.example 1194\n{directive}\n",
            null,
            "10.8.0.4",
            "device",
            null);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => converter.ConvertAsync(config, server, CancellationToken.None));

        Assert.Contains("unsupported directive", ex.Message);
        Assert.False(File.Exists(Path.Combine(XdgPaths.VpnCredentialDirectory, "libreguard-openvpn-test-fr-9.ovpn")));
    }

    [Fact]
    public async Task ConvertAsync_RejectsUnsupportedInlineBlock()
    {
        var converter = new OpenVpnProfileConverter(new FakeDeviceIdentityService("secret-pass"));
        var server = new VpnServer(10, "Test ES", "10.0.0.4", "es.example", "Spain", "Madrid", 100, "free", 20, 2, 443, true);
        var config = new VpnConfigResponse(
            true,
            "OpenVPN",
            "Test ES",
            "10.0.0.4",
            "cert",
            "client\nremote es.example 1194\n<connection>\nremote backup.example 1194\n</connection>\n",
            null,
            "10.8.0.5",
            "device",
            null);

        var ex = await Assert.ThrowsAsync<VpnConfigurationException>(() => converter.ConvertAsync(config, server, CancellationToken.None));

        Assert.Contains("unsupported inline block", ex.Message);
        Assert.False(File.Exists(Path.Combine(XdgPaths.VpnCredentialDirectory, "libreguard-openvpn-test-es-10.ovpn")));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _previousXdgStateHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousXdgConfigHome);
        Environment.SetEnvironmentVariable(
            XdgPaths.VpnCredentialDirectoryEnvironmentVariable,
            _previousVpnCredentialDirectory);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static void AssertPrivatePermissions(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        var expected = isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(expected, mode);
    }

    private sealed class FakeDeviceIdentityService(string passphrase) : IDeviceIdentityService
    {
        public Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DeviceRegistrationPayload("device", "1.0.0", "key", "key-id", "RSA-OAEP-256"));

        public Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken)
            => Task.FromResult(passphrase);
    }
}
