using System.Diagnostics;
using System.Text.Json;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class VpnSessionGuardianTests
{
    [Fact]
    public void TryParseArguments_AcceptsOnlyStrictHeadlessGuardianInvocation()
    {
        var leasePath = Path.Combine(Path.GetTempPath(), "libreguard", "lease.json");
        var nonce = Guid.NewGuid().ToString("N");

        var parsed = VpnSessionGuardianCommand.TryParseArguments(
            ["--vpn-session-guardian", "--lease", leasePath, "--nonce", nonce],
            out var parsedLeasePath,
            out var parsedNonce);

        Assert.True(parsed);
        Assert.Equal(leasePath, parsedLeasePath);
        Assert.Equal(nonce, parsedNonce);
        Assert.False(VpnSessionGuardianCommand.TryParseArguments(
            ["--vpn-session-guardian", "--lease", leasePath, "--nonce", nonce, "unexpected"],
            out _,
            out _));
    }

    [Theory]
    [InlineData("corp-vpn")]
    [InlineData("libreguard-wireguard-test")]
    [InlineData("libreguard-openvpn")]
    public void IsLibreGuardVpnProfile_RejectsProfilesOutsideOwnedPrefixes(string profileName)
    {
        Assert.False(VpnSessionGuardianCommand.IsLibreGuardVpnProfile(profileName));
    }

    [Fact]
    public void IsLibreGuardVpnProfile_AcceptsBothOwnedProtocols()
    {
        Assert.True(VpnSessionGuardianCommand.IsLibreGuardVpnProfile("libreguard-openvpn-nl-1"));
        Assert.True(VpnSessionGuardianCommand.IsLibreGuardVpnProfile("libreguard-ikev2-nl-1"));
    }

    [Fact]
    public async Task WriteLeaseAsync_ReplacesTheLeaseAtomicallyWithCompletedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var leasePath = Path.Combine(directory, "vpn-session.json");
        Directory.CreateDirectory(directory);
        try
        {
            var lease = new VpnSessionLease(
                "libreguard-openvpn-nl-1",
                Guid.NewGuid().ToString("D"),
                Environment.ProcessId,
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                Guid.NewGuid().ToString("N"),
                Completed: false);

            await VpnSessionGuardian.WriteLeaseAsync(leasePath, lease, CancellationToken.None);
            await VpnSessionGuardian.WriteLeaseAsync(leasePath, lease with { Completed = true }, CancellationToken.None);

            var actual = JsonSerializer.Deserialize<VpnSessionLease>(await File.ReadAllTextAsync(leasePath));
            Assert.NotNull(actual);
            Assert.True(actual.Completed);
            Assert.Equal(lease.ProfileName, actual.ProfileName);
            Assert.False(File.Exists(leasePath + ".new"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void IsParentAlive_RequiresTheOriginalProcessStartIdentity()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var startTicks = currentProcess.StartTime.ToUniversalTime().Ticks;

        Assert.True(VpnSessionGuardianCommand.IsParentAlive(currentProcess.Id, startTicks));
        Assert.False(VpnSessionGuardianCommand.IsParentAlive(currentProcess.Id, startTicks + 1));
    }

    [Fact]
    public async Task UnexpectedExitCleanup_CleansOnlyTheExactOwnedProfileAndLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var leasePath = Path.Combine(directory, "vpn-session.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(leasePath, "lease");
        await File.WriteAllTextAsync(leasePath + ".ready", "ready");
        try
        {
            var networkManager = new RecordingNetworkManager();
            var connectionUuid = Guid.NewGuid().ToString("D");

            var result = await VpnSessionGuardianCommand.CleanUpUnexpectedExitAsync(
                "libreguard-openvpn-nl-1",
                connectionUuid,
                connectionUuid,
                leasePath,
                networkManager,
                CancellationToken.None);

            Assert.Equal(0, result);
            Assert.Equal(
                ["available", "down:libreguard-openvpn-nl-1", "delete:libreguard-openvpn-nl-1", "artifacts:libreguard-openvpn-nl-1"],
                networkManager.Events);
            Assert.False(File.Exists(leasePath));
            Assert.False(File.Exists(leasePath + ".ready"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnexpectedExitCleanup_RefusesThirdPartyProfiles()
    {
        var networkManager = new RecordingNetworkManager();
        var connectionUuid = Guid.NewGuid().ToString("D");

        var result = await VpnSessionGuardianCommand.CleanUpUnexpectedExitAsync(
            "corp-vpn",
            connectionUuid,
            connectionUuid,
            Path.Combine(Path.GetTempPath(), "libreguard-unused-lease"),
            networkManager,
            CancellationToken.None);

        Assert.Equal(65, result);
        Assert.Empty(networkManager.Events);
    }

    [Fact]
    public async Task UnexpectedExitCleanup_DoesNotTouchARecreatedProfile()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var leasePath = Path.Combine(directory, "vpn-session.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(leasePath, "lease");
        try
        {
            var networkManager = new RecordingNetworkManager();

            var result = await VpnSessionGuardianCommand.CleanUpUnexpectedExitAsync(
                "libreguard-ikev2-de-multi-1-3",
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                leasePath,
                networkManager,
                CancellationToken.None);

            Assert.Equal(0, result);
            Assert.Empty(networkManager.Events);
            Assert.False(File.Exists(leasePath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RecordingNetworkManager : INetworkManagerClient
    {
        public List<string> Events { get; } = [];

        public Task EnsureAvailableAsync(CancellationToken cancellationToken)
        {
            Events.Add("available");
            return Task.CompletedTask;
        }

        public Task ImportOpenVpnAsync(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ImportIkeV2Async(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ActivateAsync(VpnProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeactivateAsync(string profileName, CancellationToken cancellationToken)
        {
            Events.Add($"down:{profileName}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveLibreGuardProfilesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetLibreGuardProfilesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task DisconnectLibreGuardProfilesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteLibreGuardProfilesAsync(string? excludeProfileName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupLibreGuardArtifactsAsync(string? excludeProfileName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteLibreGuardProfileAsync(string profileName, CancellationToken cancellationToken)
        {
            Events.Add($"delete:{profileName}");
            return Task.CompletedTask;
        }

        public Task CleanupLibreGuardProfileArtifactsAsync(string profileName, CancellationToken cancellationToken)
        {
            Events.Add($"artifacts:{profileName}");
            return Task.CompletedTask;
        }

        public Task<string?> GetActiveDeviceNameAsync(string profileName, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }
}
