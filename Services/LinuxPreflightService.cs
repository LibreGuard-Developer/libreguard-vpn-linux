using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class LinuxPreflightService : ILinuxPreflightService
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<bool> _isLinux;
    private readonly Func<string, bool> _fileExists;

    public LinuxPreflightService(IProcessRunner processRunner)
        : this(processRunner, OperatingSystem.IsLinux, File.Exists)
    {
    }

    public LinuxPreflightService(IProcessRunner processRunner, Func<bool> isLinux, Func<string, bool> fileExists)
    {
        _processRunner = processRunner;
        _isLinux = isLinux;
        _fileExists = fileExists;
    }

    public async Task<LinuxPreflightResult> CheckAsync(VpnProtocol protocol, CancellationToken cancellationToken)
    {
        var checks = new List<LinuxPreflightCheck>();

        if (!_isLinux())
        {
            checks.Add(new LinuxPreflightCheck(
                "Linux host",
                IsPresent: false,
                IsRequired: true,
                "LibreGuard VPN connections can only be established on Linux."));
            return new LinuxPreflightResult(checks);
        }

        checks.Add(await CommandCheckAsync(
            "NetworkManager nmcli",
            "nmcli",
            ["--version"],
            "Install NetworkManager and nmcli.",
            cancellationToken));

        checks.Add(await CommandCheckAsync(
            "Secret Service tool",
            "secret-tool",
            ["--help"],
            "Install libsecret-tools on Debian/Ubuntu or libsecret on Fedora if you want Secret Service-backed storage; LibreGuard falls back to file-backed secrets when it is unavailable.",
            cancellationToken,
            isRequired: false));

        if (protocol == VpnProtocol.OpenVpn)
        {
            checks.Add(new LinuxPreflightCheck(
                "NetworkManager OpenVPN plugin",
                AnyFileExists([
                    "/usr/lib/NetworkManager/VPN/nm-openvpn-service.name",
                    "/usr/lib64/NetworkManager/VPN/nm-openvpn-service.name",
                    "/usr/libexec/nm-openvpn-service"
                ]),
                IsRequired: true,
                "Install network-manager-openvpn on Debian/Ubuntu or NetworkManager-openvpn on Fedora."));
        }
        else
        {
            checks.Add(await CommandCheckAsync(
                "OpenSSL",
                "openssl",
                ["version"],
                "Install openssl so the IKEv2 PKCS#12 bundle can be extracted.",
                cancellationToken));

            checks.Add(new LinuxPreflightCheck(
                "NetworkManager strongSwan plugin",
                AnyFileExists([
                    "/usr/lib/NetworkManager/VPN/nm-strongswan-service.name",
                    "/usr/lib64/NetworkManager/VPN/nm-strongswan-service.name",
                    "/usr/libexec/nm-strongswan-service"
                ]),
                IsRequired: true,
                "Install network-manager-strongswan on Debian/Ubuntu or NetworkManager-strongswan on Fedora."));
        }

        checks.Add(await CommandCheckAsync(
            "Desktop opener",
            "xdg-open",
            ["--help"],
            "Install xdg-utils if browser checkout links should open automatically.",
            cancellationToken,
            isRequired: false));

        return new LinuxPreflightResult(checks);
    }

    private async Task<LinuxPreflightCheck> CommandCheckAsync(
        string name,
        string command,
        IReadOnlyList<string> arguments,
        string missingMessage,
        CancellationToken cancellationToken,
        bool isRequired = true)
    {
        var result = await _processRunner.RunAsync(command, arguments, cancellationToken);
        return new LinuxPreflightCheck(name, result.Success, isRequired, missingMessage);
    }

    private bool AnyFileExists(IEnumerable<string> paths)
        => paths.Any(_fileExists);
}
