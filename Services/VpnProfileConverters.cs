using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class OpenVpnProfileConverter(
    IDeviceIdentityService deviceIdentityService,
    IProcessRunner processRunner) : IVpnProfileConverter
{
    public OpenVpnProfileConverter(IDeviceIdentityService deviceIdentityService)
        : this(deviceIdentityService, new ProcessRunner())
    {
    }

    private static readonly HashSet<string> AllowedDirectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "askpass",
        "allow-compression",
        "allow-pull-fqdn",
        "allow-recursive-routing",
        "auth",
        "auth-nocache",
        "auth-retry",
        "auth-token",
        "auth-token-user",
        "auth-user-pass",
        "block-ipv6",
        "block-outside-dns",
        "ca",
        "cert",
        "cipher",
        "client",
        "comp-lzo",
        "compress",
        "connect-retry",
        "connect-retry-max",
        "connect-timeout",
        "data-ciphers",
        "data-ciphers-fallback",
        "dev",
        "dev-type",
        "dhcp-option",
        "dns",
        "dhcp-release",
        "dhcp-renew",
        "explicit-exit-notify",
        "fast-io",
        "float",
        "fragment",
        "hand-window",
        "http-proxy",
        "http-proxy-option",
        "http-proxy-retry",
        "ifconfig",
        "ifconfig-ipv6",
        "ifconfig-nowarn",
        "ignore-unknown-option",
        "inactive",
        "keepalive",
        "key",
        "key-direction",
        "keysize",
        "link-mtu",
        "max-routes",
        "mssfix",
        "mtu-disc",
        "mute",
        "nobind",
        "ns-cert-type",
        "persist-key",
        "persist-local-ip",
        "persist-remote-ip",
        "persist-tun",
        "ping",
        "ping-exit",
        "ping-restart",
        "pkcs12",
        "proto",
        "proto-force",
        "pull",
        "pull-filter",
        "push-peer-info",
        "rcvbuf",
        "redirect-gateway",
        "redirect-private",
        "register-dns",
        "remote",
        "remote-cert-eku",
        "remote-cert-ku",
        "remote-cert-tls",
        "remote-random",
        "remote-random-hostname",
        "reneg-sec",
        "replay-window",
        "resolv-retry",
        "route",
        "route-delay",
        "route-ipv6",
        "route-method",
        "route-metric",
        "route-nopull",
        "server-poll-timeout",
        "setenv",
        "setenv-safe",
        "sndbuf",
        "socks-proxy",
        "tcp-queue-limit",
        "tls-auth",
        "tls-cert-profile",
        "tls-cipher",
        "tls-client",
        "tls-crypt",
        "tls-crypt-v2",
        "tls-exit",
        "tls-timeout",
        "tls-version-min",
        "topology",
        "tran-window",
        "tun-mtu-extra",
        "tun-mtu",
        "txqueuelen",
        "verb",
        "verify-hash",
        "verify-x509-name",
        "windows-driver"
    };

    private static readonly HashSet<string> AllowedInlineBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "ca",
        "cert",
        "extra-certs",
        "key",
        "pkcs12",
        "tls-auth",
        "tls-crypt",
        "tls-crypt-v2"
    };

    public VpnProtocol Protocol => VpnProtocol.OpenVpn;

    public async Task<VpnProfile> ConvertAsync(VpnConfigResponse config, VpnServer server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ConfigContent))
        {
            throw new VpnConfigurationException("Backend returned an empty OpenVPN config.");
        }

        XdgPaths.EnsureAppDirectories();
        XdgPaths.EnsureVpnCredentialDirectory();
        var profileName = ProfileNames.For(server, Protocol);
        var configPath = Path.Combine(XdgPaths.VpnCredentialDirectory, $"{profileName}.ovpn");
        var secretPath = Path.Combine(XdgPaths.VpnCredentialDirectory, $"{profileName}.askpass");
        var outerTransportAddress = VpnTransportEndpoints.RequireIpv4(config, server);
        FileSystemSafety.EnsureParentDirectory(configPath);
        var content = config.ConfigContent;

        if (config.EncryptedPassphrase is not null)
        {
            var passphrase = await deviceIdentityService.DecryptPassphraseAsync(config.EncryptedPassphrase, cancellationToken);
            await File.WriteAllTextAsync(secretPath, passphrase, cancellationToken);
            TryChmod600(secretPath);
            content = content.Contains("[ENCRYPTED_PASSPHRASE]", StringComparison.Ordinal)
                ? content.Replace("[ENCRYPTED_PASSPHRASE]", secretPath, StringComparison.Ordinal)
                : $"{content.TrimEnd()}\naskpass {secretPath}\n";
        }

        content = SanitizeOpenVpnConfig(content);
        content = PrivateDnsPolicy.NormalizeOpenVpnConfig(content);
        await File.WriteAllTextAsync(configPath, content, cancellationToken);
        TryChmod600(configPath);
        await LinuxVpnCredentialSecurity.EnsureReadyAsync(processRunner, cancellationToken);

        return new VpnProfile(Protocol, profileName, configPath, secretPath, NetworkManagerVpnData: null, outerTransportAddress);
    }

    private static string SanitizeOpenVpnConfig(string config)
    {
        var lines = config.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);
        var sanitized = new List<string>(lines.Length);
        string? inlineBlock = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (inlineBlock is not null)
            {
                sanitized.Add(line);
                if (IsClosingInlineBlock(trimmed, inlineBlock))
                {
                    inlineBlock = null;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                sanitized.Add(line);
                continue;
            }

            var openingBlock = GetOpeningInlineBlockName(trimmed);
            if (openingBlock is not null)
            {
                if (!AllowedInlineBlocks.Contains(openingBlock))
                {
                    throw new VpnConfigurationException($"OpenVPN config includes unsupported inline block '<{openingBlock}>'. Request a fresh profile and try again.");
                }

                sanitized.Add(line);
                inlineBlock = openingBlock;
                continue;
            }

            if (trimmed.StartsWith("</", StringComparison.Ordinal))
            {
                throw new VpnConfigurationException("OpenVPN config includes an inline block closing tag without a matching allowed opening tag. Request a fresh profile and try again.");
            }

            var directive = GetDirectiveName(trimmed);
            if (!AllowedDirectives.Contains(directive))
            {
                throw new VpnConfigurationException($"OpenVPN config includes unsupported directive '{directive}'. Request a fresh profile and try again.");
            }

            sanitized.Add(line);
        }

        if (inlineBlock is not null)
        {
            throw new VpnConfigurationException($"OpenVPN config includes an unterminated '<{inlineBlock}>' inline block. Request a fresh profile and try again.");
        }

        return string.Join('\n', sanitized);
    }

    private static string GetDirectiveName(string trimmedLine)
    {
        var separator = trimmedLine.IndexOfAny([' ', '\t']);
        var directive = separator < 0 ? trimmedLine : trimmedLine[..separator];
        return directive.StartsWith("--", StringComparison.Ordinal) ? directive[2..] : directive;
    }

    private static string? GetOpeningInlineBlockName(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('<') || !trimmedLine.EndsWith('>') || trimmedLine.StartsWith("</", StringComparison.Ordinal))
        {
            return null;
        }

        var name = trimmedLine[1..^1].Trim();
        return name.Length == 0 || name.Any(char.IsWhiteSpace) ? null : name;
    }

    private static bool IsClosingInlineBlock(string trimmedLine, string expectedName)
    {
        if (!trimmedLine.StartsWith("</", StringComparison.Ordinal) || !trimmedLine.EndsWith('>'))
        {
            return false;
        }

        var name = trimmedLine[2..^1].Trim();
        return string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryChmod600(string path)
        => FileSecurity.EnsurePrivateFile(path);
}

public sealed class IkeV2ProfileConverter(
    IDeviceIdentityService deviceIdentityService,
    IProcessRunner processRunner,
    ISettingsStore? settingsStore = null) : IVpnProfileConverter
{
    private const string GatewayCaPathsEnvironmentVariable = "LIBREGUARD_IKEV2_CA_CERT_PATHS";
    private const string RootX1Sha256 = "96BCEC06264976F37460779ACF28C5A7CFE8A3C0AAE11A8FFCEE05C0BDDF08C6";
    private const string RootX2Sha256 = "69729B8E15A86EFC177A57AFB7171DFC64ADD28C2FCA8CF1507E34453CCB1470";
    private const string RootYeSha256 = "E14FFCAD5B0025731006CAA43A121A22D8E9700F4FB9CF852F02A708AA5D5666";
    private const string RootYrSha256 = "E57B7E6F150C419102E8D5C055729FF967B9D1A829BF00CEC89CA604EBF4A86F";
    private const string CrossSignedRootYeSha256 = "0FC0901CCA2BAE9E9FDBB02D50D02F1094F7B36672086991B9E897626DC485F0";
    private const string CrossSignedRootYrSha256 = "072639D0B140D5BFFAE16AD9C3F6CC6086040621F51EE61A6D46A8915C07CF76";
    private static readonly string[] PinnedLetsEncryptRootCommonNames = ["ISRG Root X1", "ISRG Root X2", "Root YE", "Root YR"];

    public VpnProtocol Protocol => VpnProtocol.Ikev2;

    public async Task<VpnProfile> ConvertAsync(VpnConfigResponse config, VpnServer server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ConfigContent))
        {
            throw new VpnConfigurationException("Backend returned an empty IKEv2 config.");
        }

        XdgPaths.EnsureAppDirectories();
        XdgPaths.EnsureIkeV2CredentialDirectory();
        var profileName = ProfileNames.For(server, Protocol);
        var outerTransportAddress = VpnTransportEndpoints.RequireIpv4(config, server);
        var configPath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.sswan");
        FileSystemSafety.EnsureParentDirectory(configPath);
        await File.WriteAllTextAsync(configPath, config.ConfigContent, cancellationToken);
        TryChmod600(configPath);

        using var document = JsonDocument.Parse(config.ConfigContent);
        var root = document.RootElement;
        var configuredRemoteAddress = FindString(root, "remote", "addr")
            ?? FindString(root, "remote", "address")
            ?? FindString(root, "server")
            ?? server.ServerHostname
            ?? config.ServerIp
            ?? server.ServerIp;
        var remoteIdentity = FindString(root, "remote", "id")
            ?? (System.Net.IPAddress.TryParse(configuredRemoteAddress, out _)
                ? server.ServerHostname ?? configuredRemoteAddress
                : configuredRemoteAddress);
        var remotePort = FindString(root, "remote", "port");
        var localIdentity = FindString(root, "local", "id");
        var ikeProposal = FindString(root, "ike-proposal");
        var espProposal = FindString(root, "esp-proposal");

        var passphrase = config.EncryptedPassphrase is null
            ? FindString(root, "password") ?? string.Empty
            : await deviceIdentityService.DecryptPassphraseAsync(config.EncryptedPassphrase, cancellationToken);

        if (string.IsNullOrWhiteSpace(passphrase) || passphrase == "[ENCRYPTED_PASSPHRASE]")
        {
            throw new VpnConfigurationException("IKEv2 config passphrase was not delivered for this device. Login again so the backend can register the device public key.");
        }

        var p12Bytes = FindPkcs12Bytes(root);
        if (p12Bytes is null)
        {
            throw new VpnConfigurationException("IKEv2 config does not include PKCS#12 material. Request a new certificate or verify the backend .sswan payload.");
        }

        var p12Path = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.p12");
        var passPath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.p12.pass");
        FileSystemSafety.EnsureParentDirectory(p12Path);
        await File.WriteAllBytesAsync(p12Path, p12Bytes, cancellationToken);
        await File.WriteAllTextAsync(passPath, passphrase, cancellationToken);
        TryChmod600(p12Path);
        TryChmod600(passPath);

        var certPath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.crt");
        var keyPath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.key");
        var caPath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.ca.crt");
        var p12IncludedCa = await ExtractPkcs12Async(p12Path, passPath, certPath, keyPath, caPath, cancellationToken);

        var gatewayCertificateMaterial = await BuildGatewayCertificateBundleAsync(
            root,
            profileName,
            p12IncludedCa ? caPath : null,
            server.ServerHostname ?? configuredRemoteAddress,
            cancellationToken);
        var certificatePath = gatewayCertificateMaterial?.PrimaryPath;

        var vpnDataItems = new List<string>
        {
            $"address={EscapeVpnData(outerTransportAddress)}",
            $"usercert={EscapeVpnData(certPath)}",
            $"userkey={EscapeVpnData(keyPath)}",
            "method=key",
            "ipcomp=no",
            "virtual=yes",
            "encap=no",
            "remote-ts=0.0.0.0/0"
        };

        if (!string.IsNullOrWhiteSpace(remotePort))
        {
            vpnDataItems.Add($"server-port={EscapeVpnData(remotePort)}");
        }

        if (!string.IsNullOrWhiteSpace(remoteIdentity))
        {
            vpnDataItems.Add($"remote-identity={EscapeVpnData(remoteIdentity)}");
        }

        if (!string.IsNullOrWhiteSpace(localIdentity))
        {
            vpnDataItems.Add($"local-identity={EscapeVpnData(localIdentity)}");
        }

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            vpnDataItems.Add($"certificate={EscapeVpnData(certificatePath)}");
        }

        if (!string.IsNullOrWhiteSpace(ikeProposal) || !string.IsNullOrWhiteSpace(espProposal))
        {
            vpnDataItems.Add("proposal=yes");
            if (!string.IsNullOrWhiteSpace(ikeProposal))
            {
                vpnDataItems.Add($"ike={EscapeVpnData(ikeProposal)}");
            }

            if (!string.IsNullOrWhiteSpace(espProposal))
            {
                vpnDataItems.Add($"esp={EscapeVpnData(espProposal)}");
            }
        }

        var vpnData = string.Join(",", vpnDataItems);
        await LinuxVpnCredentialSecurity.EnsureReadyAsync(
            processRunner,
            XdgPaths.IkeV2CredentialDirectory,
            cancellationToken);

        var credentialPaths = new List<string> { certPath, keyPath };
        if (gatewayCertificateMaterial?.CandidatePaths is { Count: > 0 } gatewayCertificatePaths)
        {
            credentialPaths.AddRange(gatewayCertificatePaths);
        }

        return new VpnProfile(
            Protocol,
            profileName,
            configPath,
            passPath,
            vpnData,
            outerTransportAddress,
            gatewayCertificateMaterial?.CandidatePaths,
            gatewayCertificateMaterial?.AllowPinnedGatewayRootFallback ?? false,
            outerTransportAddress,
            credentialPaths.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task<bool> ExtractPkcs12Async(string p12Path, string passPath, string certPath, string keyPath, string caPath, CancellationToken cancellationToken)
    {
        FileSystemSafety.EnsureParentDirectory(certPath);
        FileSystemSafety.EnsureParentDirectory(keyPath);
        FileSystemSafety.EnsureParentDirectory(caPath);
        var cert = await processRunner.RunAsync("openssl", [
            "pkcs12",
            "-in",
            p12Path,
            "-clcerts",
            "-nokeys",
            "-out",
            certPath,
            "-passin",
            $"file:{passPath}"
        ], cancellationToken);

        var key = await processRunner.RunAsync("openssl", [
            "pkcs12",
            "-in",
            p12Path,
            "-nocerts",
            "-nodes",
            "-out",
            keyPath,
            "-passin",
            $"file:{passPath}"
        ], cancellationToken);

        var ca = await processRunner.RunAsync("openssl", [
            "pkcs12",
            "-in",
            p12Path,
            "-cacerts",
            "-nokeys",
            "-out",
            caPath,
            "-passin",
            $"file:{passPath}"
        ], cancellationToken);

        if (!cert.Success || !key.Success)
        {
            throw new VpnConfigurationException("OpenSSL could not extract the IKEv2 PKCS#12 certificate bundle. Install openssl and request a fresh certificate if the bundle is invalid.");
        }

        if (!FileContains(certPath, "-----BEGIN CERTIFICATE-----")
            || !FileContains(keyPath, "-----BEGIN")
            || !FileContains(keyPath, "PRIVATE KEY-----"))
        {
            throw new VpnConfigurationException("OpenSSL extracted an invalid IKEv2 client certificate or private key. Request a fresh certificate and try again.");
        }

        await ValidateClientCertificateAndKeyAsync(certPath, keyPath, cancellationToken);

        TryChmod600(certPath);
        TryChmod600(keyPath);
        TryChmod600(caPath);
        return ca.Success && FileContains(caPath, "-----BEGIN CERTIFICATE-----");
    }

    private async Task ValidateClientCertificateAndKeyAsync(
        string certPath,
        string keyPath,
        CancellationToken cancellationToken)
    {
        var validity = await processRunner.RunAsync(
            "openssl",
            ["x509", "-in", certPath, "-dates", "-noout"],
            cancellationToken);
        if (!validity.Success
            || !TryParseCertificateDate(validity.StandardOutput, "notBefore", out var notBefore)
            || !TryParseCertificateDate(validity.StandardOutput, "notAfter", out var notAfter)
            || DateTimeOffset.UtcNow < notBefore
            || DateTimeOffset.UtcNow > notAfter)
        {
            throw new VpnConfigurationException("The IKEv2 client certificate is expired or not currently valid. Request a fresh certificate and try again.");
        }

        var certificatePublicKey = await processRunner.RunAsync(
            "openssl",
            ["x509", "-in", certPath, "-pubkey", "-noout"],
            cancellationToken);
        var privateKeyPublicKey = await processRunner.RunAsync(
            "openssl",
            ["pkey", "-in", keyPath, "-pubout"],
            cancellationToken);
        if (!certificatePublicKey.Success
            || !privateKeyPublicKey.Success
            || !string.Equals(
                NormalizePublicKey(certificatePublicKey.StandardOutput),
                NormalizePublicKey(privateKeyPublicKey.StandardOutput),
                StringComparison.Ordinal))
        {
            throw new VpnConfigurationException("The IKEv2 client certificate does not match its private key. Request a fresh certificate and try again.");
        }
    }

    private static string NormalizePublicKey(string value)
        => Regex.Replace(value, @"\s+", string.Empty, RegexOptions.CultureInvariant);

    private static bool TryParseCertificateDate(string output, string fieldName, out DateTimeOffset value)
    {
        value = default;
        var prefix = fieldName + "=";
        var line = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var dateText = line is null
            ? string.Empty
            : Regex.Replace(line[prefix.Length..].Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        return DateTimeOffset.TryParseExact(
                dateText,
                "MMM d HH:mm:ss yyyy 'GMT'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
    }

    private static string? FindString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }

    private static byte[]? FindPkcs12Bytes(JsonElement element)
        => FindPkcs12Bytes(element, preferPkcs12Names: true);

    private static byte[]? FindPkcs12Bytes(JsonElement element, bool preferPkcs12Names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && (!preferPkcs12Names || IsLikelyPkcs12PropertyName(property.Name))
                    && TryDecodePkcs12Payload(property.Value.GetString(), out var bytes))
                {
                    return bytes;
                }

                var nested = FindPkcs12Bytes(property.Value, preferPkcs12Names);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindPkcs12Bytes(child, preferPkcs12Names);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool IsLikelyPkcs12PropertyName(string name)
        => name.Contains("p12", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pkcs12", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pfx", StringComparison.OrdinalIgnoreCase);

    private static bool TryDecodePkcs12Payload(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            trimmed = trimmed[(comma + 1)..];
        }

        trimmed = trimmed
            .Replace("-----BEGIN PKCS12-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-----END PKCS12-----", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();

        try
        {
            var decoded = Convert.FromBase64String(trimmed);
            if (decoded.Length > 16 && decoded[0] == 0x30)
            {
                bytes = decoded;
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }

    private static string EscapeVpnData(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    private async Task<GatewayCertificateMaterial?> BuildGatewayCertificateBundleAsync(
        JsonElement root,
        string profileName,
        string? pkcs12CaPath,
        string? host,
        CancellationToken cancellationToken)
    {
        var remoteCertPath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.remote.crt");
        var bundlePath = Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.gateway-ca-bundle.crt");

        var configuredPaths = GetConfiguredGatewayCertificatePaths().ToArray();
        var configuredTrustWasRequested = configuredPaths.Length > 0;
        var remoteRootCommonName = TryInferPinnedRootFromRemoteCertificate(root, host);
        var approvedCrossSignedRoot = SelectApprovedCrossSignedRootFromRemotePayload(root);
        var remoteCertificatePath = await TryWriteRemoteCertificateAsync(root, remoteCertPath, cancellationToken);
        var remoteCertificates = string.IsNullOrWhiteSpace(remoteCertificatePath)
            ? Array.Empty<string>()
            : ReadCaCertificatesFromPath(remoteCertificatePath).ToArray();
        approvedCrossSignedRoot ??= SelectApprovedCrossSignedLetsEncryptRoot(remoteCertificates);
        var pkcs12Certificates = string.IsNullOrWhiteSpace(pkcs12CaPath)
            ? Array.Empty<string>()
            : ReadCaCertificatesFromPath(pkcs12CaPath).ToArray();

        var certificates = new List<string>();
        foreach (var configuredPath in configuredPaths)
        {
            AddCertificates(certificates, ReadCaCertificatesFromPath(configuredPath));
        }

        var allowPinnedGatewayRootFallback = false;
        if (!configuredTrustWasRequested
            && !string.IsNullOrWhiteSpace(approvedCrossSignedRoot))
        {
            certificates.Clear();
            AddCertificates(certificates, new[] { approvedCrossSignedRoot! });
            Trace.WriteLine(
                $"LibreGuard IKEv2: preserving the approved cross-signed gateway root for '{host ?? "unknown gateway"}'.");
        }
        else if (!configuredTrustWasRequested
            && !string.IsNullOrWhiteSpace(remoteRootCommonName))
        {
            certificates.Clear();
            AddCertificates(certificates, GetPinnedLetsEncryptRootCertificates(remoteRootCommonName));
            allowPinnedGatewayRootFallback = true;
        }

        if (certificates.Count == 0 && !configuredTrustWasRequested)
        {
            AddCertificates(certificates, remoteCertificates);
        }

        if (certificates.Count == 0 && !configuredTrustWasRequested)
        {
            var systemCertificates = ReadKnownLetsEncryptCaCertificatesFromSystemPaths().ToArray();
            if (systemCertificates.Length > 0)
            {
                AddCertificates(certificates, GetPinnedLetsEncryptRootCertificates(remoteRootCommonName));
                allowPinnedGatewayRootFallback = true;
            }
        }

        if (certificates.Count == 0)
        {
            if (!configuredTrustWasRequested && !string.IsNullOrWhiteSpace(remoteRootCommonName))
            {
                AddCertificates(certificates, GetPinnedLetsEncryptRootCertificates(remoteRootCommonName));
                allowPinnedGatewayRootFallback = true;
            }
            else if (!configuredTrustWasRequested)
            {
                AddCertificates(certificates, GetPinnedLetsEncryptRootCertificates(preferredRootCommonName: null));
                allowPinnedGatewayRootFallback = true;
            }
        }

        // The PKCS#12 CA normally issues the client certificate; it is not
        // necessarily the VPN gateway CA. Keep the d4304b9 ordering and use it
        // only when no configured, gateway-provided, or pinned trust is usable.
        if (certificates.Count == 0 && pkcs12Certificates.Length > 0)
        {
            AddCertificates(certificates, pkcs12Certificates);
        }

        if (certificates.Count == 0)
        {
            Trace.WriteLine(
                $"LibreGuard IKEv2: no gateway CA bundle could be built for '{host ?? "unknown gateway"}'. "
                + $"Set {GatewayCaPathsEnvironmentVariable} to one or more PEM files containing the server's issuing CA chain.");
            return null;
        }

        if (allowPinnedGatewayRootFallback && settingsStore is not null)
        {
            await PrioritizeRememberedGatewayRootAsync(
                certificates,
                profileName,
                settingsStore,
                cancellationToken);
        }

        FileSystemSafety.EnsureParentDirectory(bundlePath);
        var candidatePaths = new List<string>(certificates.Count);
        for (var index = 0; index < certificates.Count; index++)
        {
            var path = index == 0
                ? bundlePath
                : Path.Combine(XdgPaths.IkeV2CredentialDirectory, $"{profileName}.gateway-ca-{index}.crt");
            await File.WriteAllTextAsync(path, certificates[index], cancellationToken);
            TryChmod600(path);
            candidatePaths.Add(path);
        }

        // NetworkManager-strongSwan passes this file to charon-nm as one
        // certificate object. Keep each candidate in its own PEM file because
        // strongSwan only reads the first certificate from a PEM bundle.
        return new GatewayCertificateMaterial(bundlePath, candidatePaths, allowPinnedGatewayRootFallback);
    }

    private static async Task PrioritizeRememberedGatewayRootAsync(
        List<string> certificates,
        string profileName,
        ISettingsStore preferenceSettings,
        CancellationToken cancellationToken)
    {
        string? preferredFingerprint;
        try
        {
            preferredFingerprint = await preferenceSettings.GetAsync<string>(
                IkeV2GatewayTrustPreference.SettingsKey(profileName),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(preferredFingerprint))
        {
            return;
        }

        var preferredIndex = certificates.FindIndex(certificate =>
            string.Equals(
                GetCertificateSha256(certificate),
                preferredFingerprint,
                StringComparison.OrdinalIgnoreCase));
        if (preferredIndex <= 0)
        {
            return;
        }

        var preferredCertificate = certificates[preferredIndex];
        certificates.RemoveAt(preferredIndex);
        certificates.Insert(0, preferredCertificate);
    }

    private static string? GetCertificateSha256(string certificatePem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            return certificate.GetCertHashString(HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record GatewayCertificateMaterial(
        string PrimaryPath,
        IReadOnlyList<string> CandidatePaths,
        bool AllowPinnedGatewayRootFallback);

    private static async Task<string?> TryWriteRemoteCertificateAsync(JsonElement root, string path, CancellationToken cancellationToken)
    {
        var remoteCert = FindString(root, "remote", "cert");
        if (!TryNormalizeCertificatePayload(remoteCert, out var certificate)
            || !ContainsLikelyCaCertificate(certificate))
        {
            return null;
        }

        FileSystemSafety.EnsureParentDirectory(path);
        await File.WriteAllTextAsync(path, certificate, cancellationToken);
        TryChmod600(path);
        return path;
    }

    private static string? TryInferPinnedRootFromRemoteCertificate(JsonElement root, string? host)
    {
        var remoteCert = FindString(root, "remote", "cert");
        if (!TryNormalizeCertificatePayload(remoteCert, out var normalizedPayload))
        {
            return null;
        }

        var parsedCertificates = new List<ParsedGatewayCertificate>();
        foreach (var pem in ExtractPemCertificates(normalizedPayload))
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                parsedCertificates.Add(new ParsedGatewayCertificate(
                    pem,
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
                    IsCertificateAuthority(certificate),
                    string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal),
                    certificate.GetCertHashString(HashAlgorithmName.SHA256)));
            }
            catch (CryptographicException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        if (parsedCertificates.Count == 0)
        {
            return null;
        }

        foreach (var certificate in parsedCertificates)
        {
            if (!IsKnownLetsEncryptRoot(certificate.SubjectCommonName))
            {
                continue;
            }

            if (certificate.SelfSigned || IsApprovedCrossSignedLetsEncryptRoot(certificate))
            {
                return certificate.SubjectCommonName;
            }
        }

        foreach (var certificate in parsedCertificates)
        {
            var rootCommonName = ResolveLetsEncryptRoot(certificate, parsedCertificates);
            if (!string.IsNullOrWhiteSpace(rootCommonName))
            {
                return rootCommonName;
            }
        }

        Trace.WriteLine(
            $"LibreGuard IKEv2: remote.cert for '{host ?? "unknown gateway"}' did not identify a pinned Let's Encrypt hierarchy.");
        return null;
    }

    private static string? SelectApprovedCrossSignedLetsEncryptRoot(IEnumerable<string> certificates)
    {
        foreach (var pem in certificates)
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                var parsed = new ParsedGatewayCertificate(
                    pem,
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
                    IsCertificateAuthority(certificate),
                    string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal),
                    certificate.GetCertHashString(HashAlgorithmName.SHA256));
                if (IsApprovedCrossSignedLetsEncryptRoot(parsed))
                {
                    return pem;
                }
            }
            catch (CryptographicException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string? SelectApprovedCrossSignedRootFromRemotePayload(JsonElement root)
    {
        var remoteCert = FindString(root, "remote", "cert");
        if (!TryNormalizeCertificatePayload(remoteCert, out var normalizedPayload))
        {
            return null;
        }

        return SelectApprovedCrossSignedLetsEncryptRoot(ExtractPemCertificates(normalizedPayload));
    }

    private static string? ResolveLetsEncryptRoot(
        ParsedGatewayCertificate certificate,
        IReadOnlyList<ParsedGatewayCertificate> suppliedCertificates)
    {
        if (IsKnownLetsEncryptRoot(certificate.IssuerCommonName))
        {
            return certificate.IssuerCommonName;
        }

        if (Regex.IsMatch(certificate.SubjectCommonName, @"^YE\d+$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(certificate.IssuerCommonName, @"^YE\d+$", RegexOptions.CultureInvariant))
        {
            return "Root YE";
        }

        if (Regex.IsMatch(certificate.SubjectCommonName, @"^YR\d+$", RegexOptions.CultureInvariant)
            || Regex.IsMatch(certificate.IssuerCommonName, @"^YR\d+$", RegexOptions.CultureInvariant))
        {
            return "Root YR";
        }

        if (Regex.IsMatch(certificate.IssuerCommonName, @"^(?:E7|E8|R12|R13)$", RegexOptions.CultureInvariant))
        {
            var suppliedIntermediate = suppliedCertificates.FirstOrDefault(candidate =>
                string.Equals(candidate.SubjectCommonName, certificate.IssuerCommonName, StringComparison.Ordinal));
            return suppliedIntermediate is not null && IsKnownLetsEncryptRoot(suppliedIntermediate.IssuerCommonName)
                ? suppliedIntermediate.IssuerCommonName
                : "ISRG Root X1";
        }

        return null;
    }

    private static bool IsKnownLetsEncryptRoot(string commonName)
        => PinnedLetsEncryptRootCommonNames.Contains(commonName, StringComparer.Ordinal);

    private static bool IsApprovedCrossSignedLetsEncryptRoot(ParsedGatewayCertificate certificate)
    {
        var expectedFingerprint = certificate.SubjectCommonName switch
        {
            "Root YE" when string.Equals(certificate.IssuerCommonName, "ISRG Root X2", StringComparison.Ordinal)
                => CrossSignedRootYeSha256,
            "Root YR" when string.Equals(certificate.IssuerCommonName, "ISRG Root X1", StringComparison.Ordinal)
                => CrossSignedRootYrSha256,
            _ => null
        };

        return expectedFingerprint is not null
            && certificate.CertificateAuthority
            && string.Equals(certificate.Sha256, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate)
        => certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Any(extension => extension.CertificateAuthority);

    private sealed record ParsedGatewayCertificate(
        string Pem,
        string SubjectCommonName,
        string IssuerCommonName,
        bool CertificateAuthority,
        bool SelfSigned,
        string Sha256);

    private static void AddCertificates(List<string> destination, IEnumerable<string> source)
    {
        foreach (var certificate in source)
        {
            if (!destination.Contains(certificate, StringComparer.Ordinal))
            {
                destination.Add(certificate);
            }
        }
    }

    private static IEnumerable<string> GetPinnedLetsEncryptRootCertificates(string? preferredRootCommonName)
    {
        var orderedNames = (string.IsNullOrWhiteSpace(preferredRootCommonName)
                ? PinnedLetsEncryptRootCommonNames
                : new[] { preferredRootCommonName })
            .Concat(PinnedLetsEncryptRootCommonNames)
            .Distinct(StringComparer.Ordinal);
        foreach (var commonName in orderedNames)
        {
            yield return GetBundledLetsEncryptRootCertificate(commonName);
        }
    }

    private static string GetBundledLetsEncryptRootCertificate(string commonName)
    {
        foreach (var pem in LetsEncryptGatewayTrustAssets.EnumerateCertificates())
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                if (!string.Equals(
                        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                        commonName,
                        StringComparison.Ordinal)
                    || !string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal))
                {
                    continue;
                }

                ValidateBundledRootFingerprint(commonName, certificate);
                return pem;
            }
            catch (CryptographicException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        throw new VpnConfigurationException($"LibreGuard does not include the required IKEv2 trust anchor '{commonName}'.");
    }

    private static void ValidateBundledRootFingerprint(string commonName, X509Certificate2 certificate)
    {
        var expectedFingerprint = commonName switch
        {
            "ISRG Root X1" => RootX1Sha256,
            "ISRG Root X2" => RootX2Sha256,
            "Root YE" => RootYeSha256,
            "Root YR" => RootYrSha256,
            _ => null
        };
        if (expectedFingerprint is null
            || !string.Equals(
                certificate.GetCertHashString(HashAlgorithmName.SHA256),
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new VpnConfigurationException($"LibreGuard's bundled IKEv2 trust anchor '{commonName}' failed fingerprint validation.");
        }
    }

    private static IEnumerable<string> GetConfiguredGatewayCertificatePaths()
    {
        var configuredPaths = Environment.GetEnvironmentVariable(GatewayCaPathsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPaths))
        {
            yield break;
        }

        foreach (var path in configuredPaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (FileContains(path, "-----BEGIN CERTIFICATE-----"))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> ReadKnownLetsEncryptCaCertificatesFromSystemPaths()
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in GetSystemGatewayCertificateCandidates())
        {
            if (seenPaths.Add(path))
            {
                foreach (var certificate in ReadKnownLetsEncryptCaCertificatesFromPath(path))
                {
                    yield return certificate;
                }
            }
        }
    }

    private static IEnumerable<string> GetSystemGatewayCertificateCandidates()
    {
        yield return "/etc/ssl/certs/ca-certificates.crt";
        yield return "/etc/pki/tls/certs/ca-bundle.crt";
        yield return "/etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem";
        yield return "/etc/ssl/ca-bundle.pem";
        yield return "/etc/ssl/cert.pem";
        yield return "/etc/ssl/certs/ISRG_Root_X1.pem";
        yield return "/etc/ssl/certs/ISRG_Root_X2.pem";
        yield return "/etc/ssl/certs/ISRG_Root_YE.pem";
        yield return "/etc/ssl/certs/ISRG_Root_YR.pem";
        yield return "/usr/share/ca-certificates/mozilla/ISRG_Root_X1.crt";
        yield return "/usr/share/ca-certificates/mozilla/ISRG_Root_X2.crt";
        yield return "/usr/share/ca-certificates/mozilla/ISRG_Root_YE.crt";
        yield return "/usr/share/ca-certificates/mozilla/ISRG_Root_YR.crt";
        yield return "/etc/ca-certificates/trust-source/anchors/ISRG_Root_X1.pem";
        yield return "/etc/ca-certificates/trust-source/anchors/ISRG_Root_X2.pem";
        yield return "/etc/ca-certificates/trust-source/anchors/ISRG_Root_YE.pem";
        yield return "/etc/ca-certificates/trust-source/anchors/ISRG_Root_YR.pem";
        yield return "/usr/local/share/ca-certificates/ISRG_Root_X1.crt";
        yield return "/usr/local/share/ca-certificates/ISRG_Root_X2.crt";
        yield return "/usr/local/share/ca-certificates/ISRG_Root_YE.crt";
        yield return "/usr/local/share/ca-certificates/ISRG_Root_YR.crt";
        yield return "/usr/local/share/ca-certificates/root-ye-by-x2.crt";
        yield return "/usr/local/share/ca-certificates/root-yr-by-x1.crt";

        foreach (var directory in GetSystemGatewayCertificateDirectories())
        {
            foreach (var path in EnumerateCertificateFiles(directory))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> GetSystemGatewayCertificateDirectories()
    {
        yield return "/etc/ssl/certs";
        yield return "/usr/share/ca-certificates";
        yield return "/usr/local/share/ca-certificates";
        yield return "/etc/ca-certificates/trust-source/anchors";
        yield return "/etc/pki/ca-trust/source/anchors";
    }

    private static IEnumerable<string> EnumerateCertificateFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        IEnumerable<string> Enumerate(string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
        }

        foreach (var pattern in new[] { "*.crt", "*.pem" })
        {
            foreach (var file in Enumerate(pattern))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> ExtractPemCertificates(string value)
    {
        foreach (Match match in Regex.Matches(
                     value,
                     "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----",
                     RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            yield return NormalizePemCertificate(match.Value);
        }
    }

    private static bool ContainsLikelyCaCertificate(string value)
        => ExtractPemCertificates(value).Any(IsLikelyCaCertificate);

    private static IEnumerable<string> ReadKnownLetsEncryptCaCertificatesFromPath(string path)
        => ReadCaCertificatesFromPath(path, LetsEncryptGatewayTrustAssets.HasKnownSubject);

    private static IEnumerable<string> ReadCaCertificatesFromPath(string path)
        => ReadCaCertificatesFromPath(path, static _ => true);

    private static IEnumerable<string> ReadCaCertificatesFromPath(string path, Func<string, bool> filter)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return ExtractPemCertificates(File.ReadAllText(path))
                .Where(IsLikelyCaCertificate)
                .Where(filter)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsLikelyCaCertificate(string certificate)
    {
        try
        {
            using var x509 = X509Certificate2.CreateFromPem(certificate);
            foreach (var extension in x509.Extensions.OfType<X509BasicConstraintsExtension>())
            {
                return extension.CertificateAuthority;
            }
        }
        catch (CryptographicException)
        {
        }
        catch (ArgumentException)
        {
        }

        var body = certificate.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("-----BEGIN CERTIFICATE-----", string.Empty, StringComparison.Ordinal)
            .Replace("-----END CERTIFICATE-----", string.Empty, StringComparison.Ordinal);
        return body.Contains("root", StringComparison.OrdinalIgnoreCase)
            || body.Contains("intermediate", StringComparison.OrdinalIgnoreCase)
            || body.Contains("ca-", StringComparison.OrdinalIgnoreCase)
            || body.Contains("ca_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSelfSignedCertificate(string certificate)
    {
        try
        {
            using var x509 = X509Certificate2.CreateFromPem(certificate);
            return string.Equals(x509.Subject, x509.Issuer, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryNormalizeCertificatePayload(string? value, out string certificate)
    {
        certificate = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("-----BEGIN CERTIFICATE-----", StringComparison.OrdinalIgnoreCase))
        {
            certificate = NormalizePemCertificate(trimmed);
            return true;
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            trimmed = trimmed[(comma + 1)..];
        }

        trimmed = string.Concat(trimmed.Where(ch => !char.IsWhiteSpace(ch)));
        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            var decodedText = Encoding.ASCII.GetString(bytes);
            if (decodedText.Contains("-----BEGIN CERTIFICATE-----", StringComparison.OrdinalIgnoreCase))
            {
                certificate = NormalizePemCertificate(decodedText);
                return true;
            }

            if (bytes.Length > 16 && bytes[0] == 0x30)
            {
                certificate = ToPem("CERTIFICATE", bytes);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }

    private static string NormalizePemCertificate(string pem)
        => pem.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n";

    private static string ToPem(string label, byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        var builder = new StringBuilder();
        builder.Append("-----BEGIN ").Append(label).AppendLine("-----");
        for (var index = 0; index < base64.Length; index += 64)
        {
            builder.AppendLine(base64.Substring(index, Math.Min(64, base64.Length - index)));
        }

        builder.Append("-----END ").Append(label).AppendLine("-----");
        return builder.ToString();
    }

    private static bool FileContains(string path, string value)
    {
        try
        {
            return File.Exists(path)
                && File.ReadAllText(path).Contains(value, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryChmod600(string path)
        => FileSecurity.EnsurePrivateFile(path);
}

internal static class VpnTransportEndpoints
{
    public static string RequireIpv4(VpnConfigResponse config, VpnServer server)
    {
        foreach (var candidate in new[] { config.ServerIp, server.ServerIp })
        {
            if (IPAddress.TryParse(candidate, out var address)
                && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return address.ToString();
            }
        }

        throw new VpnConfigurationException("LibreGuard requires a literal IPv4 server address from the backend to establish the VPN without public DNS or IPv6 leakage.");
    }
}

internal static class IkeV2GatewayTrustPreference
{
    internal static string SettingsKey(string profileName)
        => $"ikev2-gateway-trust-v1:{profileName}";
}

internal static class FileSystemSafety
{
    public static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            FileSecurity.EnsurePrivateDirectory(directory);
        }

        FileSecurity.EnsureNotSymbolicLink(path);
    }
}

internal static class ProfileNames
{
    public static string For(VpnServer server, VpnProtocol protocol)
    {
        var protocolName = protocol == VpnProtocol.OpenVpn ? "openvpn" : "ikev2";
        var name = $"libreguard-{protocolName}-{server.ServerName}-{server.Id}".ToLowerInvariant();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-');
        }

        return builder.ToString();
    }
}
