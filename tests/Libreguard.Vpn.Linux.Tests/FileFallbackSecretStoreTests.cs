using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class FileFallbackSecretStoreTests : IDisposable
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SharedPermissionBits =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string? _previousXdgStateHome;
    private readonly string? _previousXdgConfigHome;

    public FileFallbackSecretStoreTests()
    {
        _previousXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        _previousXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(_tempRoot, "state"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_tempRoot, "config"));
    }

    [Fact]
    public async Task SetAndGetAsync_RoundTripsFallbackSecret()
    {
        var store = new FileFallbackSecretStore();
        Assert.False(store.HasPersistedFallbackSelection);

        await store.SetAsync("auth-token", "token-value", CancellationToken.None);
        await store.SetAsync("device-key", "device-value", CancellationToken.None);

        Assert.False(store.HasPersistedFallbackSelection);
        Assert.Equal("token-value", await store.GetAsync("auth-token", CancellationToken.None));
        Assert.Equal("device-value", await store.GetAsync("device-key", CancellationToken.None));
    }

    [Fact]
    public async Task PersistedBackendMarker_SelectsFallbackOnNextLaunch()
    {
        var store = new FileFallbackSecretStore();

        await store.SetAsync(
            CompositeSecretStore.FallbackMarkerKey,
            CompositeSecretStore.FallbackMarkerValue,
            CancellationToken.None);

        Assert.True(new FileFallbackSecretStore().HasPersistedFallbackSelection);
    }

    [Fact]
    public async Task SetAsync_CreatesPrivateSecretFileOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var store = new FileFallbackSecretStore();

        await store.SetAsync("auth-token", "token-value", CancellationToken.None);

        AssertPrivateLinuxFileMode(GetSecretFilePath());
    }

    [Fact]
    public async Task GetAsync_MigratesPermissiveExistingSecretFileOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(XdgPaths.AppConfigDirectory);
        var secretFile = GetSecretFilePath();
        await File.WriteAllTextAsync(secretFile, """{"auth-token":"token-value"}""");
        File.SetUnixFileMode(secretFile, PrivateFileMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var store = new FileFallbackSecretStore();

        Assert.Equal("token-value", await store.GetAsync("auth-token", CancellationToken.None));

        AssertPrivateLinuxFileMode(secretFile);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", _previousXdgStateHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousXdgConfigHome);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static string GetSecretFilePath()
        => Path.Combine(XdgPaths.AppConfigDirectory, "dev-secrets.json");

    private static void AssertPrivateLinuxFileMode(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(PrivateFileMode, mode & (PrivateFileMode | SharedPermissionBits));
    }
}
