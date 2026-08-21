using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class LinuxPreflightServiceTests
{
    [Fact]
    public async Task CheckAsync_ReportsMissingProtocolPlugin()
    {
        var runner = new SuccessfulCommandRunner();
        var service = new LinuxPreflightService(runner, isLinux: () => true, fileExists: _ => false);

        var result = await service.CheckAsync(VpnProtocol.OpenVpn, CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("network-manager-openvpn", result.Summary);
        Assert.Contains("NetworkManager-openvpn", result.Summary);
        Assert.Contains(runner.Commands, command => command.FileName == "nmcli");
        Assert.Contains(runner.Commands, command => command.FileName == "secret-tool");
    }

    [Fact]
    public async Task CheckAsync_AllowsReadyIkev2Host_WhenSecretToolIsUnavailable()
    {
        var runner = new ConfiguredCommandRunner(fileName => fileName == "secret-tool"
            ? new ProcessResult(127, string.Empty, "secret-tool not found")
            : new ProcessResult(0, "ok", string.Empty));
        var service = new LinuxPreflightService(
            runner,
            isLinux: () => true,
            fileExists: path => path.Contains("nm-strongswan-service", StringComparison.Ordinal));

        var result = await service.CheckAsync(VpnProtocol.Ikev2, CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal("Linux VPN dependencies are ready.", result.Summary);
        Assert.Contains(result.Checks, check => check.Name == "Secret Service tool" && !check.IsRequired && !check.IsPresent);
        Assert.Contains(runner.Commands, command => command.FileName == "secret-tool");
    }

    [Fact]
    public async Task CheckAsync_ReportsMissingNmcli()
    {
        var runner = new ConfiguredCommandRunner(fileName => fileName == "nmcli"
            ? new ProcessResult(127, string.Empty, "nmcli not found")
            : new ProcessResult(0, "ok", string.Empty));
        var service = new LinuxPreflightService(
            runner,
            isLinux: () => true,
            fileExists: _ => true);

        var result = await service.CheckAsync(VpnProtocol.OpenVpn, CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains("Install NetworkManager and nmcli.", result.Summary);
    }

    [Theory]
    [InlineData(VpnProtocol.OpenVpn, "/usr/lib64/NetworkManager/VPN/nm-openvpn-service.name", "NetworkManager-openvpn")]
    [InlineData(VpnProtocol.OpenVpn, "/usr/libexec/nm-openvpn-service", "NetworkManager-openvpn")]
    [InlineData(VpnProtocol.Ikev2, "/usr/lib64/NetworkManager/VPN/nm-strongswan-service.name", "NetworkManager-strongswan")]
    [InlineData(VpnProtocol.Ikev2, "/usr/libexec/nm-strongswan-service", "NetworkManager-strongswan")]
    public async Task CheckAsync_RecognizesFedoraPluginPathsAndNamesPackages(
        VpnProtocol protocol,
        string installedPluginPath,
        string fedoraPackage)
    {
        var runner = new SuccessfulCommandRunner();
        var service = new LinuxPreflightService(
            runner,
            isLinux: () => true,
            fileExists: path => string.Equals(path, installedPluginPath, StringComparison.Ordinal));

        var result = await service.CheckAsync(protocol, CancellationToken.None);

        Assert.True(result.IsReady);
        var pluginCheck = Assert.Single(
            result.Checks,
            check => check.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase));
        Assert.True(pluginCheck.IsPresent);
        Assert.Contains(fedoraPackage, pluginCheck.Message, StringComparison.Ordinal);
        var secretCheck = Assert.Single(result.Checks, check => check.Name == "Secret Service tool");
        Assert.Contains("libsecret on Fedora", secretCheck.Message, StringComparison.Ordinal);
    }

    private sealed class SuccessfulCommandRunner : IProcessRunner
    {
        public List<CommandRecord> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            Commands.Add(new CommandRecord(fileName, arguments.ToArray()));
            return Task.FromResult(new ProcessResult(0, "ok", string.Empty));
        }
    }

    private sealed class ConfiguredCommandRunner(Func<string, ProcessResult> outcomeFactory) : IProcessRunner
    {
        public List<CommandRecord> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            Commands.Add(new CommandRecord(fileName, arguments.ToArray()));
            return Task.FromResult(outcomeFactory(fileName));
        }
    }

    private sealed record CommandRecord(string FileName, IReadOnlyList<string> Arguments);
}
