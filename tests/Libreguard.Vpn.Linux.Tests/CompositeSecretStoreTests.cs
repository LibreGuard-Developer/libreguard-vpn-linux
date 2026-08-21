using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class CompositeSecretStoreTests
{
    [Fact]
    public async Task PreferredFallback_DoesNotProbeUnavailableKeyring()
    {
        var primary = new RecordingSecretStore { ThrowOnEveryOperation = true };
        var fallback = new RecordingSecretStore();
        fallback.Values["jwt-token"] = "file-token";
        var store = new CompositeSecretStore(primary, fallback, preferFallback: true);

        var value = await store.GetAsync("jwt-token", CancellationToken.None);
        await store.SetAsync("refresh-token", "file-refresh", CancellationToken.None);
        await store.DeleteAsync("jwt-token", CancellationToken.None);

        Assert.Equal("file-token", value);
        Assert.Empty(primary.Operations);
        Assert.Equal("file-refresh", fallback.Values["refresh-token"]);
        Assert.DoesNotContain("jwt-token", fallback.Values.Keys);
    }

    [Fact]
    public async Task KeyringFailure_SwitchesToFallbackForRestOfProcessAndPersistsChoice()
    {
        var primary = new RecordingSecretStore { ThrowOnEveryOperation = true };
        var fallback = new RecordingSecretStore();
        fallback.Values["first"] = "one";
        fallback.Values["second"] = "two";
        var diagnostics = new List<string>();
        var store = new CompositeSecretStore(primary, fallback, diagnosticSink: diagnostics.Add);

        Assert.Equal("one", await store.GetAsync("first", CancellationToken.None));
        Assert.Equal("two", await store.GetAsync("second", CancellationToken.None));

        Assert.Single(primary.Operations);
        Assert.Contains(fallback.Values, entry =>
            entry.Key.StartsWith("__libreguard-secret-store-backend", StringComparison.Ordinal)
            && entry.Value == "file");
        Assert.Contains(diagnostics, line => line.Contains("secret-service-unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AvailableKeyring_RemainsPrimaryStore()
    {
        var primary = new RecordingSecretStore();
        var fallback = new RecordingSecretStore();
        var store = new CompositeSecretStore(primary, fallback);

        await store.SetAsync("jwt-token", "keyring-token", CancellationToken.None);
        var value = await store.GetAsync("jwt-token", CancellationToken.None);

        Assert.Equal("keyring-token", value);
        Assert.DoesNotContain("jwt-token", fallback.Values.Keys);
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public List<string> Operations { get; } = [];
        public bool ThrowOnEveryOperation { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Operations.Add($"get:{key}");
            ThrowIfUnavailable();
            return Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Operations.Add($"set:{key}");
            ThrowIfUnavailable();
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Operations.Add($"delete:{key}");
            ThrowIfUnavailable();
            Values.Remove(key);
            return Task.CompletedTask;
        }

        private void ThrowIfUnavailable()
        {
            if (ThrowOnEveryOperation)
            {
                throw new InvalidOperationException("Secret Service is unavailable.");
            }
        }
    }
}
