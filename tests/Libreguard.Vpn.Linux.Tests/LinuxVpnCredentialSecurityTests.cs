using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class LinuxVpnCredentialSecurityTests
{
    [Fact]
    public void ResolveVpnCredentialDirectory_DefaultsToKnownGoodStateLocation()
    {
        var home = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var path = XdgPaths.ResolveVpnCredentialDirectory(null, home);

        Assert.Equal(Path.Combine(home, ".local", "state", "libreguard", "configs"), path);
    }

    [Fact]
    public void ResolveVpnCredentialDirectory_UsesExplicitOverride()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "credentials");

        var path = XdgPaths.ResolveVpnCredentialDirectory(overridePath, "ignored");

        Assert.Equal(Path.GetFullPath(overridePath), path);
    }

    [Fact]
    public void ResolveIkeV2CredentialDirectory_DefaultsToSelinuxCertificateLocation()
    {
        var home = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var path = XdgPaths.ResolveIkeV2CredentialDirectory(null, home);

        Assert.Equal(Path.Combine(home, ".cert", "libreguard"), path);
    }

    [Fact]
    public void ResolveIkeV2CredentialDirectory_PreservesExplicitOverride()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "credentials");

        var path = XdgPaths.ResolveIkeV2CredentialDirectory(overridePath, "ignored");

        Assert.Equal(Path.GetFullPath(overridePath), path);
    }

    [Fact]
    public async Task EnsureReadyAsync_RestoresAndVerifiesSelinuxContexts()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var credential = Path.Combine(directory, "profile.key");
        await File.WriteAllTextAsync(credential, "test");
        var runner = new RecordingRunner();

        try
        {
            await LinuxVpnCredentialSecurity.EnsureReadyAsync(
                runner,
                directory,
                isLinux: true,
                CancellationToken.None);

            Assert.Collection(
                runner.Commands,
                command =>
                {
                    Assert.Equal("getenforce", command.FileName);
                    Assert.Empty(command.Arguments);
                },
                command =>
                {
                    Assert.Equal("restorecon", command.FileName);
                    Assert.Equal(["-RF", directory], command.Arguments);
                },
                command =>
                {
                    Assert.Equal("matchpathcon", command.FileName);
                    Assert.Equal(["-V", directory], command.Arguments);
                },
                command =>
                {
                    Assert.Equal("matchpathcon", command.FileName);
                    Assert.Equal(["-V", credential], command.Arguments);
                });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureReadyAsync_FailsWhenEnforcingContextCannotBeRestored()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var runner = new RecordingRunner
        {
            RestoreResult = new ProcessResult(1, string.Empty, "restore failed")
        };

        var exception = await Assert.ThrowsAsync<VpnConfigurationException>(() =>
            LinuxVpnCredentialSecurity.EnsureReadyAsync(
                runner,
                directory,
                isLinux: true,
                CancellationToken.None));

        Assert.Contains("policycoreutils", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("libselinux-utils", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(directory, exception.Message, StringComparison.Ordinal);
        Assert.Contains("restore failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureReadyAsync_DoesNotBlockPermissiveSelinuxOnLabelFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var runner = new RecordingRunner
        {
            EnforceResult = new ProcessResult(0, "Permissive\n", string.Empty),
            RestoreResult = new ProcessResult(1, string.Empty, "restore failed")
        };

        await LinuxVpnCredentialSecurity.EnsureReadyAsync(
            runner,
            directory,
            isLinux: true,
            CancellationToken.None);

        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task EnsureReadyAsync_SkipsPolicyCommandsWhenSelinuxIsUnavailable()
    {
        var runner = new RecordingRunner
        {
            EnforceResult = new ProcessResult(127, string.Empty, "not found")
        };

        await LinuxVpnCredentialSecurity.EnsureReadyAsync(
            runner,
            "/unused",
            isLinux: true,
            CancellationToken.None);

        Assert.Single(runner.Commands);
        Assert.Equal("getenforce", runner.Commands[0].FileName);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<CommandRecord> Commands { get; } = [];
        public ProcessResult EnforceResult { get; init; } = new(0, "Enforcing\n", string.Empty);
        public ProcessResult RestoreResult { get; init; } = new(0, string.Empty, string.Empty);

        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken)
        {
            Commands.Add(new CommandRecord(fileName, arguments.ToArray()));
            return Task.FromResult(fileName switch
            {
                "getenforce" => EnforceResult,
                "restorecon" => RestoreResult,
                _ => new ProcessResult(0, string.Empty, string.Empty)
            });
        }
    }

    private sealed record CommandRecord(string FileName, IReadOnlyList<string> Arguments);
}
