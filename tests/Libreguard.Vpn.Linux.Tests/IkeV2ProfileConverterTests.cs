using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class IkeV2ProfileConverterTests : IDisposable
{
    private const string GatewayCaPathsEnvironmentVariable = "LIBREGUARD_IKEV2_CA_CERT_PATHS";
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string? _previousXdgStateHome;
    private readonly string? _previousXdgConfigHome;
    private readonly string? _previousVpnCredentialDirectory;

    public IkeV2ProfileConverterTests()
    {
        _previousXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        _previousXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _previousVpnCredentialDirectory = Environment.GetEnvironmentVariable(XdgPaths.VpnCredentialDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(GatewayCaPathsEnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(_tempRoot, "state"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_tempRoot, "config"));
        Environment.SetEnvironmentVariable(
            XdgPaths.VpnCredentialDirectoryEnvironmentVariable,
            Path.Combine(_tempRoot, "credentials"));
    }

    [Fact]
    public async Task ConvertAsync_FindsPkcs12PayloadAndBuildsNetworkManagerData()
    {
        var p12 = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        var remoteCert = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 });
        var content = $$"""
            {
              "remote": {
                "addr": "vpn.example.com",
                "id": "vpn-id.example.com",
                "port": 4500,
                "cert": "{{remoteCert}}"
              },
              "password": "local-pass",
              "payload": {
                "pkcs12": "{{p12}}"
              },
              "ike-proposal": "aes256-sha256-modp2048",
              "esp-proposal": "aes256-sha256"
            }
            """;
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());
        var server = new VpnServer(9, "IKE Test", "10.0.0.9", "ike.example", "Netherlands", "Amsterdam", 100, "free", 10, 1, 443, true);
        var config = new VpnConfigResponse(true, "IKEV2", "IKE Test", "10.0.0.9", "cert", content, null, null, "device", null);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        Assert.Contains("address=10.0.0.9", profile.NetworkManagerVpnData);
        Assert.DoesNotContain("address=vpn.example.com", profile.NetworkManagerVpnData);
        Assert.Equal("10.0.0.9", profile.Ikev2RemoteAddress);
        Assert.Equal("10.0.0.9", profile.OuterTransportAddress);
        Assert.Contains("remote-identity=vpn-id.example.com", profile.NetworkManagerVpnData);
        Assert.Contains("server-port=4500", profile.NetworkManagerVpnData);
        Assert.Contains("certificate=", profile.NetworkManagerVpnData);
        Assert.Contains("usercert=", profile.NetworkManagerVpnData);
        Assert.Contains("userkey=", profile.NetworkManagerVpnData);
        Assert.Contains("method=key", profile.NetworkManagerVpnData);
        Assert.Contains("proposal=yes", profile.NetworkManagerVpnData);
        Assert.Contains("ike=aes256-sha256-modp2048", profile.NetworkManagerVpnData);
        Assert.Contains("esp=aes256-sha256", profile.NetworkManagerVpnData);
        Assert.Contains("ipcomp=no", profile.NetworkManagerVpnData);
        Assert.Contains("encap=no", profile.NetworkManagerVpnData);
        Assert.Contains("remote-ts=0.0.0.0/0", profile.NetworkManagerVpnData);
        Assert.DoesNotContain("::/0", profile.NetworkManagerVpnData);
        var profileDirectory = Path.GetDirectoryName(profile.ConfigPath) ?? throw new InvalidOperationException("Profile path had no directory.");
        Assert.True(File.Exists(Path.Combine(profileDirectory, $"{profile.ProfileName}.p12")));
        Assert.Equal(XdgPaths.IkeV2CredentialDirectory, profileDirectory);
        Assert.NotNull(profile.Ikev2CredentialPaths);
        Assert.Contains(Path.Combine(profileDirectory, $"{profile.ProfileName}.crt"), profile.Ikev2CredentialPaths!);
        Assert.Contains(Path.Combine(profileDirectory, $"{profile.ProfileName}.key"), profile.Ikev2CredentialPaths!);
        Assert.All(profile.Ikev2GatewayCertificatePaths ?? [], path =>
            Assert.Contains(path, profile.Ikev2CredentialPaths!));
        AssertPrivatePermissions(profileDirectory, isDirectory: true);
        foreach (var path in Directory.EnumerateFiles(profileDirectory))
        {
            AssertPrivatePermissions(path, isDirectory: false);
        }
    }

    [Fact]
    public async Task ConvertAsync_UsesBackendIpv4WhenConfiguredRemoteIsLinkLocalIpv6()
    {
        var p12 = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        var content = $$"""
            {
              "remote": {
                "addr": "fe80::1:6aff:fed8:6cbe",
                "id": "de-multi-1.libreguard.net"
              },
              "password": "local-pass",
              "payload": {
                "pkcs12": "{{p12}}"
              }
            }
            """;
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());
        var server = new VpnServer(3, "DE Multi 1", "198.51.100.31", "de-multi-1.libreguard.net", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var config = new VpnConfigResponse(true, "IKEV2", server.ServerName, server.ServerIp, "cert", content, null, null, "device", null);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        Assert.Contains("address=198.51.100.31", profile.NetworkManagerVpnData);
        Assert.DoesNotContain("address=fe80::1:6aff:fed8:6cbe", profile.NetworkManagerVpnData);
        Assert.Contains("remote-identity=de-multi-1.libreguard.net", profile.NetworkManagerVpnData);
        Assert.Equal("198.51.100.31", profile.Ikev2RemoteAddress);
        Assert.Equal("198.51.100.31", profile.OuterTransportAddress);
    }

    [Fact]
    public async Task ConvertAsync_UsesServerHostnameWhenRemoteAddressIsMissing()
    {
        var profile = await ConvertWithBundledTrustAsync("vpn.example.org");

        Assert.Contains("address=10.0.0.15", profile.NetworkManagerVpnData);
        Assert.DoesNotContain("address=vpn.example.org", profile.NetworkManagerVpnData);
        Assert.Equal("10.0.0.15", profile.Ikev2RemoteAddress);
        Assert.Equal("10.0.0.15", profile.OuterTransportAddress);
        Assert.Contains("remote-identity=vpn.example.org", profile.NetworkManagerVpnData);

        var certificates = LoadCertificates(GetCertificatePath(profile));
        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=ISRG Root X1", StringComparison.Ordinal));
        Assert.Single(certificates);
        Assert.NotNull(profile.Ikev2GatewayCertificatePaths);
        Assert.True(profile.Ikev2AllowPinnedGatewayRootFallback);
        Assert.Equal(4, profile.Ikev2GatewayCertificatePaths!.Count);
        Assert.Contains(profile.Ikev2GatewayCertificatePaths!, path =>
            LoadCertificates(path).Any(certificate => certificate.Subject.Contains("CN=Root YE", StringComparison.Ordinal)));
        Assert.Contains(profile.Ikev2GatewayCertificatePaths!, path =>
            LoadCertificates(path).Any(certificate => certificate.Subject.Contains("CN=Root YR", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ConvertAsync_UsesConfiguredTrustAnchorBeforePkcs12AndBundledFallback()
    {
        var rootCaPath = Path.Combine(_tempRoot, "isrg-root-ye.pem");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(rootCaPath, "-----BEGIN CERTIFICATE-----\nroot-ca\n-----END CERTIFICATE-----\n");
        Environment.SetEnvironmentVariable(GatewayCaPathsEnvironmentVariable, rootCaPath);

        var converter = new IkeV2ProfileConverter(
            new FakeDeviceIdentityService("device-pass"),
            new CustomOpenSslRunner(caContent: "-----BEGIN CERTIFICATE-----\nintermediate-ca\n-----END CERTIFICATE-----\n"));
        var server = new VpnServer(14, "IKE Bundle Test", "10.0.0.14", "vpn-bundle.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var config = CreateMinimalIkeConfig(server, includePkcs12: true);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var bundle = await File.ReadAllTextAsync(GetCertificatePath(profile));
        Assert.Contains("root-ca", bundle);
        Assert.DoesNotContain("intermediate-ca", bundle);
        Assert.False(profile.Ikev2AllowPinnedGatewayRootFallback);
    }

    [Fact]
    public async Task ConvertAsync_DeduplicatesMergedCaCertificates()
    {
        var duplicateCa = "-----BEGIN CERTIFICATE-----\nintermediate-ca\n-----END CERTIFICATE-----\n";
        var rootCaPath = Path.Combine(_tempRoot, "duplicate-ca.pem");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(rootCaPath, duplicateCa);
        Environment.SetEnvironmentVariable(GatewayCaPathsEnvironmentVariable, rootCaPath);

        var converter = new IkeV2ProfileConverter(
            new FakeDeviceIdentityService("device-pass"),
            new CustomOpenSslRunner(caContent: duplicateCa));
        var server = new VpnServer(16, "IKE Dedup Test", "10.0.0.16", "vpn-dedup.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var config = CreateMinimalIkeConfig(server, includePkcs12: true);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var bundle = await File.ReadAllTextAsync(GetCertificatePath(profile));
        Assert.Equal(1, CountOccurrences(bundle, "intermediate-ca"));
    }

    [Fact]
    public async Task ConvertAsync_UsesBundledLetsEncryptTrustSetWhenOtherSourcesAreUnavailable()
    {
        Environment.SetEnvironmentVariable(GatewayCaPathsEnvironmentVariable, Path.Combine(_tempRoot, "missing-ca.pem"));

        var profile = await ConvertWithBundledTrustAsync("vpn-root-ye.example.org", new CustomOpenSslRunner(caContent: string.Empty));
        var certificates = LoadCertificates(GetCertificatePath(profile));

        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=ISRG Root X1", StringComparison.Ordinal));
        Assert.Single(certificates);
        Assert.NotNull(profile.Ikev2GatewayCertificatePaths);
        Assert.True(profile.Ikev2AllowPinnedGatewayRootFallback);
        Assert.Equal(4, profile.Ikev2GatewayCertificatePaths!.Count);
        Assert.Contains(profile.Ikev2GatewayCertificatePaths!, path =>
            LoadCertificates(path).Any(certificate => certificate.Subject.Contains("CN=Root YE", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ConvertAsync_PrioritizesPreviouslySuccessfulPinnedGatewayRoot()
    {
        Environment.SetEnvironmentVariable(GatewayCaPathsEnvironmentVariable, Path.Combine(_tempRoot, "missing-ca.pem"));
        var server = new VpnServer(27, "Remembered YR", "10.0.0.27", "remembered-yr.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var settings = new InMemorySettingsStore();
        using var rootYr = EnumerateBundledLetsEncryptCertificates()
            .Single(certificate => certificate.Subject.Contains("CN=Root YR", StringComparison.Ordinal)
                && string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal));
        await settings.SetAsync(
            IkeV2GatewayTrustPreference.SettingsKey(ProfileNames.For(server, VpnProtocol.Ikev2)),
            rootYr.GetCertHashString(HashAlgorithmName.SHA256),
            CancellationToken.None);
        var converter = new IkeV2ProfileConverter(
            new FakeDeviceIdentityService("device-pass"),
            new CustomOpenSslRunner(caContent: string.Empty),
            settings);

        var profile = await converter.ConvertAsync(
            CreateMinimalIkeConfig(server, includePkcs12: true),
            server,
            CancellationToken.None);

        var first = Assert.Single(LoadCertificates(GetCertificatePath(profile)));
        Assert.Equal("Root YR", first.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Equal(4, profile.Ikev2GatewayCertificatePaths!.Count);
    }

    [Fact]
    public async Task ConvertAsync_CompletesYe2GatewayChainWithBundledRootYe()
    {
        var ye2 = EnumerateBundledLetsEncryptCertificates()
            .Single(certificate => certificate.Subject.Contains("CN=YE2", StringComparison.Ordinal));
        var remoteCert = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ye2.ExportCertificatePem()));
        var server = new VpnServer(17, "IKE YE2 Chain Test", "10.0.0.17", "vpn-ye2.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var p12 = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        var content = JsonSerializer.Serialize(new
        {
            remote = new { cert = remoteCert },
            password = "local-pass",
            payload = new { pkcs12 = p12 }
        });
        var config = new VpnConfigResponse(true, "IKEV2", server.ServerName, server.ServerIp, "cert", content, null, null, "device", null);
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);
        var certificates = LoadCertificates(GetCertificatePath(profile));

        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=Root YE", StringComparison.Ordinal));
        Assert.Single(certificates);
        Assert.NotNull(profile.Ikev2GatewayCertificatePaths);
        Assert.True(profile.Ikev2AllowPinnedGatewayRootFallback);
        Assert.Equal(4, profile.Ikev2GatewayCertificatePaths!.Count);
        Assert.Contains(profile.Ikev2GatewayCertificatePaths!, path =>
            LoadCertificates(path).Any(certificate => certificate.Subject.Contains("CN=Root YR", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ConvertAsync_PreservesApprovedCrossSignedRootYeFromRemoteCertificate()
    {
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());
        var server = new VpnServer(19, "IKE Cross-Signed Root Test", "10.0.0.19", "gb-multi-1.libreguard.net", "United Kingdom", "London", 100, "free", 10, 1, 443, true);
        var config = CreateIkeConfigWithRemoteCertificate(
            server,
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(CrossSignedRootYePem)));

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var certificatePath = GetCertificatePath(profile);
        var certificate = Assert.Single(LoadCertificates(certificatePath));
        Assert.Equal("Root YE", certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Equal("ISRG Root X2", certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
        Assert.Equal("0FC0901CCA2BAE9E9FDBB02D50D02F1094F7B36672086991B9E897626DC485F0", certificate.GetCertHashString(HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task ConvertAsync_SelectsRootYrFirstWhenRemoteIssuerIsYr()
    {
        var yr2 = EnumerateBundledLetsEncryptCertificates()
            .Single(certificate => certificate.Subject.Contains("CN=YR2", StringComparison.Ordinal));
        var remoteCert = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(yr2.ExportCertificatePem()));
        var server = new VpnServer(20, "IKE YR Chain Test", "10.0.0.20", "vpn-yr2.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var p12 = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        var content = JsonSerializer.Serialize(new
        {
            remote = new { cert = remoteCert },
            password = "local-pass",
            payload = new { pkcs12 = p12 }
        });
        var config = new VpnConfigResponse(true, "IKEV2", server.ServerName, server.ServerIp, "cert", content, null, null, "device", null);
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);
        var first = Assert.Single(LoadCertificates(GetCertificatePath(profile)));

        Assert.Equal("Root YR", first.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.True(profile.Ikev2AllowPinnedGatewayRootFallback);
        Assert.Equal(4, profile.Ikev2GatewayCertificatePaths!.Count);
        Assert.Equal("Root YR", LoadCertificates(profile.Ikev2GatewayCertificatePaths[0]).Single().GetNameInfo(X509NameType.SimpleName, false));
    }

    [Fact]
    public async Task ConvertAsync_UsesBundledGatewayTrustBeforeClientPkcs12Ca()
    {
        var profile = await ConvertWithBundledTrustAsync(
            "vpn-pkcs12-ca.example.org",
            new CustomOpenSslRunner(caContent: "-----BEGIN CERTIFICATE-----\nintermediate-ca\n-----END CERTIFICATE-----\n"));

        var storedCa = Assert.Single(LoadCertificates(GetCertificatePath(profile)));
        Assert.Equal("ISRG Root X1", storedCa.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.DoesNotContain("intermediate-ca", await File.ReadAllTextAsync(GetCertificatePath(profile)));
    }

    [Fact]
    public async Task ConvertAsync_E7GatewayTrustOverridesThunderGradClientCertificateCa()
    {
        var e7 = EnumerateBundledLetsEncryptCertificates()
            .Single(certificate => certificate.Subject.Contains("CN=E7", StringComparison.Ordinal)
                && certificate.Issuer.Contains("CN=ISRG Root X1", StringComparison.Ordinal));
        using var thunderGradKey = RSA.Create(2048);
        var thunderGradRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=ThunderGradVPN-Root-CA",
            thunderGradKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        thunderGradRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        thunderGradRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var thunderGradCa = thunderGradRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var server = new VpnServer(24, "Standard E7 IKE", "95.217.191.191", "fl-multi-1.libreguard.net", "Finland", "Helsinki", 100, "free", 10, 1, 443, true);
        var config = CreateIkeConfigWithRemoteCertificate(
            server,
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(e7.ExportCertificatePem())));
        var converter = new IkeV2ProfileConverter(
            new FakeDeviceIdentityService("device-pass"),
            new CustomOpenSslRunner(caContent: thunderGradCa.ExportCertificatePem()));

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var storedCa = Assert.Single(LoadCertificates(GetCertificatePath(profile)));
        Assert.Equal("ISRG Root X1", storedCa.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.DoesNotContain("ThunderGradVPN-Root-CA", storedCa.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertAsync_PreservesStandardGatewayCaAsSingleTrustAnchor()
    {
        using var key = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=ThunderGradVPN-Root-CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var gatewayCa = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var server = new VpnServer(23, "Standard IKE", "10.0.0.23", "standard.example.org", "Finland", "Helsinki", 100, "free", 10, 1, 443, true);
        var config = CreateIkeConfigWithRemoteCertificate(
            server,
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(gatewayCa.ExportCertificatePem())));
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var storedCa = Assert.Single(LoadCertificates(GetCertificatePath(profile)));
        Assert.Contains("CN=ThunderGradVPN-Root-CA", storedCa.Subject, StringComparison.Ordinal);
        Assert.Single(profile.Ikev2GatewayCertificatePaths!);
        Assert.False(profile.Ikev2AllowPinnedGatewayRootFallback);
        Assert.Contains("address=10.0.0.23", profile.NetworkManagerVpnData);
        Assert.Contains("remote-identity=standard.example.org", profile.NetworkManagerVpnData);
        Assert.DoesNotContain("::/0", profile.NetworkManagerVpnData);
    }

    [Fact]
    public void LetsEncryptTrustAssets_IncludeE7AndE8CertificatesForBothX1AndX2Chains()
    {
        var certificates = EnumerateBundledLetsEncryptCertificates()
            .Where(certificate => certificate.Subject.Contains("CN=E7", StringComparison.Ordinal)
                || certificate.Subject.Contains("CN=E8", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=E7", StringComparison.Ordinal)
            && certificate.Issuer.Contains("CN=ISRG Root X1", StringComparison.Ordinal));
        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=E7", StringComparison.Ordinal)
            && certificate.Issuer.Contains("CN=ISRG Root X2", StringComparison.Ordinal));
        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=E8", StringComparison.Ordinal)
            && certificate.Issuer.Contains("CN=ISRG Root X1", StringComparison.Ordinal));
        Assert.Contains(certificates, certificate => certificate.Subject.Contains("CN=E8", StringComparison.Ordinal)
            && certificate.Issuer.Contains("CN=ISRG Root X2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertAsync_DoesNotProbeHttpsForGatewayTrust()
    {
        var runner = new SuccessfulOpenSslRunner();
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), runner);
        var server = new VpnServer(11, "IKE No Probe Test", "10.0.0.11", "vpn-no-probe.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var config = CreateMinimalIkeConfig(server, includePkcs12: true);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        Assert.Contains("address=10.0.0.11", profile.NetworkManagerVpnData);
        Assert.Contains("remote-identity=vpn-no-probe.example.org", profile.NetworkManagerVpnData);
        Assert.Equal("10.0.0.11", profile.OuterTransportAddress);
        Assert.False(runner.GatewayCertificateProbeUsed);
    }

    [Fact]
    public async Task ConvertAsync_RejectsExpiredClientCertificateBeforeImport()
    {
        var server = new VpnServer(21, "Expired Client", "10.0.0.21", "expired.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var converter = new IkeV2ProfileConverter(
            new FakeDeviceIdentityService("device-pass"),
            new CustomOpenSslRunner(certificateValid: false));

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            converter.ConvertAsync(CreateMinimalIkeConfig(server, includePkcs12: true), server, CancellationToken.None));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertAsync_RejectsMismatchedClientCertificateAndPrivateKeyBeforeImport()
    {
        var server = new VpnServer(22, "Mismatched Client", "10.0.0.22", "mismatch.example.org", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var converter = new IkeV2ProfileConverter(
            new FakeDeviceIdentityService("device-pass"),
            new CustomOpenSslRunner(keysMatch: false));

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            converter.ConvertAsync(CreateMinimalIkeConfig(server, includePkcs12: true), server, CancellationToken.None));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertAsync_UsesRemoteCertificateOnlyWhenItContainsCaMaterial()
    {
        var p12 = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        var remoteCert = Convert.ToBase64String("-----BEGIN CERTIFICATE-----\nleaf-server\n-----END CERTIFICATE-----\n"u8.ToArray());
        var content = $$"""
            {
              "remote": {
                "addr": "vpn-ye.example.com",
                "cert": "{{remoteCert}}"
              },
              "password": "local-pass",
              "payload": {
                "pkcs12": "{{p12}}"
              }
            }
            """;
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), new SuccessfulOpenSslRunner());
        var server = new VpnServer(13, "IKE YE Test", "10.0.0.13", "vpn-ye.example.com", "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var config = new VpnConfigResponse(true, "IKEV2", "IKE YE Test", "10.0.0.13", "cert", content, null, null, "device", null);

        var profile = await converter.ConvertAsync(config, server, CancellationToken.None);

        var bundle = await File.ReadAllTextAsync(GetCertificatePath(profile));
        Assert.DoesNotContain("leaf-server", bundle);
        Assert.DoesNotContain($"{profile.ProfileName}.remote.crt", profile.NetworkManagerVpnData);
    }

    [Fact]
    public async Task ReadKnownLetsEncryptCaCertificatesFromPath_ReadsConcatenatedSystemBundle()
    {
        var profile = await ConvertWithBundledTrustAsync("vpn-system-bundle.example.org");
        var firstCertificate = ExtractFirstCertificate(await File.ReadAllTextAsync(GetCertificatePath(profile)));
        var path = Path.Combine(_tempRoot, "system-ca-bundle.crt");
        await File.WriteAllTextAsync(path, $"{firstCertificate}\n-----BEGIN CERTIFICATE-----\nunknown-ca\n-----END CERTIFICATE-----\n");

        var method = typeof(IkeV2ProfileConverter).GetMethod(
            "ReadKnownLetsEncryptCaCertificatesFromPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("System bundle reader was not found.");

        var certificates = ((IEnumerable<string>)method.Invoke(null, [path])!).ToArray();

        Assert.Single(certificates);
        Assert.Equal(firstCertificate, certificates[0]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GatewayCaPathsEnvironmentVariable, null);
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

    private async Task<VpnProfile> ConvertWithBundledTrustAsync(string host, IProcessRunner? runner = null)
    {
        var converter = new IkeV2ProfileConverter(new FakeDeviceIdentityService("device-pass"), runner ?? new SuccessfulOpenSslRunner());
        var server = new VpnServer(15, "IKE Bundled Trust Test", "10.0.0.15", host, "Germany", "Frankfurt", 100, "free", 10, 1, 443, true);
        var config = CreateMinimalIkeConfig(server, includePkcs12: true);
        return await converter.ConvertAsync(config, server, CancellationToken.None);
    }

    private static VpnConfigResponse CreateMinimalIkeConfig(VpnServer server, bool includePkcs12)
    {
        var p12 = includePkcs12
            ? Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 })
            : string.Empty;
        var content = JsonSerializer.Serialize(new
        {
            password = "local-pass",
            payload = new { pkcs12 = p12 }
        });

        return new VpnConfigResponse(true, "IKEV2", server.ServerName, server.ServerIp, "cert", content, null, null, "device", null);
    }

    private static VpnConfigResponse CreateIkeConfigWithRemoteCertificate(VpnServer server, string remoteCertificate)
    {
        var p12 = Convert.ToBase64String(new byte[] { 0x30, 0x82, 0x00, 0x12, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });
        var content = JsonSerializer.Serialize(new
        {
            remote = new
            {
                addr = server.ServerHostname,
                cert = remoteCertificate
            },
            password = "local-pass",
            payload = new { pkcs12 = p12 }
        });
        return new VpnConfigResponse(true, "IKEV2", server.ServerName, server.ServerIp, "cert", content, null, null, "device", null);
    }

    private const string CrossSignedRootYePem = """
        -----BEGIN CERTIFICATE-----
        MIICpjCCAiugAwIBAgIRAIchZfw0tuX7qK3Vs3BftTowCgYIKoZIzj0EAwMwTzEL
        MAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2VhcmNo
        IEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDIwHhcNMjYwNTEzMDAwMDAwWhcN
        MzIwOTAyMjM1OTU5WjAuMQswCQYDVQQGEwJVUzENMAsGA1UEChMESVNSRzEQMA4G
        A1UEAxMHUm9vdCBZRTB2MBAGByqGSM49AgEGBSuBBAAiA2IABDwS/6vhrcVqcbBo
        +wgdI3fwn9x7DNJJOY/lTOti0vkwuRN87RhEhTH17E7XyFjWsPYhIPt/wzOqxTd2
        b+4ZJNy9ID04YywF9U5zasDVyGSNErVNtz8uSGh5izW87j77GaOB6zCB6DAOBgNV
        HQ8BAf8EBAMCAQYwEwYDVR0lBAwwCgYIKwYBBQUHAwEwDwYDVR0TAQH/BAUwAwEB
        /zAdBgNVHQ4EFgQUo8gmWo6hTNA1Y/ybI8g6rlbzT1YwHwYDVR0jBBgwFoAUfEKW
        rt5LSDv6kviejM9ti6lyN5UwMgYIKwYBBQUHAQEEJjAkMCIGCCsGAQUFBzAChhZo
        dHRwOi8veDIuaS5sZW5jci5vcmcvMBMGA1UdIAQMMAowCAYGZ4EMAQIBMCcGA1Ud
        HwQgMB4wHKAaoBiGFmh0dHA6Ly94Mi5jLmxlbmNyLm9yZy8wCgYIKoZIzj0EAwMD
        aQAwZgIxAMU19WCtmxVND8UHBZRoma49Z7jPs64Dma0eTu1OChVbB/2J7GV3nvYK
        Ax54uk1G9QIxAO0miLVJu8PLNiXXXkiE/gsK3CTRTF/aeo4bMX42Zw40csRU6AC2
        6hSW1/IWaas6dg==
        -----END CERTIFICATE-----
        """;

    private static List<X509Certificate2> LoadCertificates(string path)
    {
        var bundle = File.ReadAllText(path);
        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(bundle);
        return collection.Cast<X509Certificate2>().ToList();
    }

    private static List<X509Certificate2> EnumerateBundledLetsEncryptCertificates()
    {
        var type = typeof(IkeV2ProfileConverter).Assembly.GetType("Libreguard.Vpn.Linux.Services.LetsEncryptGatewayTrustAssets")
            ?? throw new InvalidOperationException("Let's Encrypt trust asset type was not found.");
        var method = type.GetMethod(
            "EnumerateCertificates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Let's Encrypt trust asset enumerator was not found.");
        var certificates = ((IEnumerable<string>)method.Invoke(null, null)!).ToArray();
        var collection = new X509Certificate2Collection();
        collection.ImportFromPem(string.Join("\n", certificates));
        return collection.Cast<X509Certificate2>().ToList();
    }

    private static string ExtractFirstCertificate(string bundle)
        => bundle.Split("-----END CERTIFICATE-----", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0].Trim()
            + "\n-----END CERTIFICATE-----\n";

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(token, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += token.Length;
        }

        return count;
    }

    private sealed class FakeDeviceIdentityService(string passphrase) : IDeviceIdentityService
    {
        public Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DeviceRegistrationPayload("device", "1.0.0", "key", "key-id", "RSA-OAEP-256"));

        public Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken)
            => Task.FromResult(passphrase);
    }

    private sealed class SuccessfulOpenSslRunner : IProcessRunner
    {
        private const string PublicKey = "-----BEGIN PUBLIC KEY-----\ntest-public-key\n-----END PUBLIC KEY-----\n";
        public bool GatewayCertificateProbeUsed { get; private set; }

        public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            if (args.Contains("s_client"))
            {
                GatewayCertificateProbeUsed = true;
                return new ProcessResult(0, "unexpected s_client call", string.Empty);
            }

            if (args.Length > 0 && args[0] == "x509" && args.Contains("-dates"))
            {
                return new ProcessResult(0, "notBefore=Jan  1 00:00:00 2020 GMT\nnotAfter=Jan  1 00:00:00 2099 GMT\n", string.Empty);
            }

            if (args.Length > 0 && args[0] == "x509" && args.Contains("-pubkey"))
            {
                return new ProcessResult(0, PublicKey, string.Empty);
            }

            if (args.Length > 0 && args[0] == "pkey" && args.Contains("-pubout"))
            {
                return new ProcessResult(0, PublicKey, string.Empty);
            }

            var outputIndex = Array.IndexOf(args, "-out");
            if (outputIndex < 0 || outputIndex >= args.Length - 1)
            {
                return new ProcessResult(1, string.Empty, "missing -out");
            }

            var outputPath = args[outputIndex + 1];
            var content = args.Contains("-nocerts")
                ? "-----BEGIN PRIVATE KEY-----\ntest\n-----END PRIVATE KEY-----\n"
                : "-----BEGIN CERTIFICATE-----\ntest\n-----END CERTIFICATE-----\n";
            await File.WriteAllTextAsync(outputPath, content, cancellationToken);
            return new ProcessResult(0, "ok", string.Empty);
        }
    }

    private sealed class CustomOpenSslRunner(
        string? certContent = null,
        string? keyContent = null,
        string? caContent = null,
        bool certificateValid = true,
        bool keysMatch = true) : IProcessRunner
    {
        private const string PublicKey = "-----BEGIN PUBLIC KEY-----\ntest-public-key\n-----END PUBLIC KEY-----\n";

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            if (args.Contains("s_client"))
            {
                throw new InvalidOperationException("s_client should not be called.");
            }

            if (args.Length > 0 && args[0] == "x509" && args.Contains("-dates"))
            {
                return Task.FromResult(certificateValid
                    ? new ProcessResult(0, "notBefore=Jan  1 00:00:00 2020 GMT\nnotAfter=Jan  1 00:00:00 2099 GMT\n", string.Empty)
                    : new ProcessResult(0, "notBefore=Jan  1 00:00:00 2000 GMT\nnotAfter=Jan  1 00:00:00 2001 GMT\n", string.Empty));
            }

            if (args.Length > 0 && args[0] == "x509" && args.Contains("-pubkey"))
            {
                return Task.FromResult(new ProcessResult(0, PublicKey, string.Empty));
            }

            if (args.Length > 0 && args[0] == "pkey" && args.Contains("-pubout"))
            {
                var publicKey = keysMatch ? PublicKey : PublicKey.Replace("test-public-key", "different-public-key", StringComparison.Ordinal);
                return Task.FromResult(new ProcessResult(0, publicKey, string.Empty));
            }

            var outputIndex = Array.IndexOf(args, "-out");
            if (outputIndex < 0 || outputIndex >= args.Length - 1)
            {
                return Task.FromResult(new ProcessResult(1, string.Empty, "missing -out"));
            }

            var outputPath = args[outputIndex + 1];
            var content = args.Contains("-clcerts")
                ? certContent ?? "-----BEGIN CERTIFICATE-----\nclient-cert\n-----END CERTIFICATE-----\n"
                : args.Contains("-nocerts")
                    ? keyContent ?? "-----BEGIN PRIVATE KEY-----\nclient-key\n-----END PRIVATE KEY-----\n"
                    : caContent ?? "-----BEGIN CERTIFICATE-----\nca-cert\n-----END CERTIFICATE-----\n";

            return WriteOutputAsync(outputPath, content, cancellationToken);
        }

        private static async Task<ProcessResult> WriteOutputAsync(string outputPath, string content, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(outputPath, content, cancellationToken);
            return new ProcessResult(0, "ok", string.Empty);
        }
    }

    private static string GetCertificatePath(VpnProfile profile)
    {
        var vpnData = profile.NetworkManagerVpnData ?? throw new InvalidOperationException("Profile did not include NetworkManager data.");
        var certificateItem = Assert.Single(vpnData.Split(','), item => item.StartsWith("certificate=", StringComparison.Ordinal));
        return certificateItem["certificate=".Length..];
    }
}
