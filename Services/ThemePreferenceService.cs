namespace Libreguard.Vpn.Linux.Services;

public sealed class ThemePreferenceService : IThemePreferenceService
{
    private const string PreferenceKey = "theme-preference";
    private readonly ISettingsStore _settingsStore;
    private AppThemePreference _currentPreference = AppThemePreference.System;

    public ThemePreferenceService(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public event EventHandler? PreferenceChanged;

    public AppThemePreference CurrentPreference => _currentPreference;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var storedValue = await _settingsStore.GetAsync<string>(PreferenceKey, cancellationToken);
        _currentPreference = AppThemePreferenceExtensions.Parse(storedValue);
    }

    public async Task SetPreferenceAsync(AppThemePreference preference, CancellationToken cancellationToken)
    {
        var changed = _currentPreference != preference;
        _currentPreference = preference;
        await _settingsStore.SetAsync(PreferenceKey, preference.ToString(), cancellationToken);

        if (changed)
        {
            PreferenceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
