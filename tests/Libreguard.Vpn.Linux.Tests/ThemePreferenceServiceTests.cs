using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class ThemePreferenceServiceTests
{
    [Fact]
    public async Task InitializeAsync_DefaultsToSystem_WhenPreferenceIsMissing()
    {
        var store = new InMemorySettingsStore();
        var service = new ThemePreferenceService(store);

        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(AppThemePreference.System, service.CurrentPreference);
    }

    [Theory]
    [InlineData("System", AppThemePreference.System)]
    [InlineData("Light", AppThemePreference.Light)]
    [InlineData("Dark", AppThemePreference.Dark)]
    [InlineData("invalid", AppThemePreference.System)]
    public async Task InitializeAsync_ParsesStoredPreference(string storedValue, AppThemePreference expectedPreference)
    {
        var store = new InMemorySettingsStore();
        await store.SetAsync("theme-preference", storedValue, CancellationToken.None);
        var service = new ThemePreferenceService(store);

        await service.InitializeAsync(CancellationToken.None);

        Assert.Equal(expectedPreference, service.CurrentPreference);
    }

    [Fact]
    public async Task SetPreferenceAsync_PersistsRoundTrip()
    {
        var store = new InMemorySettingsStore();
        var service = new ThemePreferenceService(store);

        await service.InitializeAsync(CancellationToken.None);
        await service.SetPreferenceAsync(AppThemePreference.Dark, CancellationToken.None);

        var reloaded = new ThemePreferenceService(store);
        await reloaded.InitializeAsync(CancellationToken.None);

        Assert.Equal(AppThemePreference.Dark, reloaded.CurrentPreference);
    }
}
