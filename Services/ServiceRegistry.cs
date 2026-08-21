using Libreguard.Vpn.Linux.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Libreguard.Vpn.Linux.Services;

public static class ServiceRegistry
{
    public static ServiceProvider Build()
    {
        XdgPaths.EnsureAppDirectories();
        var backendSettings = BackendSettings.Load();
        var googleOAuthSettings = GoogleOAuthSettings.Load();
        var appVersion = AppSettings.Load().AppVersion;

        var services = new ServiceCollection();
        services.AddSingleton<ISecretStore>(_ =>
        {
            var fallback = new FileFallbackSecretStore();
            return new CompositeSecretStore(
                new SecretServiceStore(),
                fallback,
                preferFallback: fallback.HasPersistedFallbackSelection,
                diagnosticSink: StartupDiagnostics.Log);
        });
        services.AddSingleton<ISettingsStore, LocalSettingsStore>();
        services.AddSingleton<ILocalStatisticsStore, LocalStatisticsStore>();
        services.AddSingleton<IThemePreferenceService, ThemePreferenceService>();
        services.AddSingleton<IDeviceIdentityService>(sp => new DeviceIdentityService(sp.GetRequiredService<ISecretStore>(), appVersion));
        services.AddSingleton<IAuthSessionService, AuthSessionService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IExternalUriLauncher, XdgExternalUriLauncher>();
        services.AddSingleton<IDesktopNotificationService, FreedesktopNotificationService>();
        services.AddSingleton<ICardCheckoutWindowService, AvaloniaCardCheckoutWindowService>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<AvaloniaFileSavePickerService>();
        services.AddSingleton<IFileSavePickerService>(sp => sp.GetRequiredService<AvaloniaFileSavePickerService>());
        services.AddSingleton<GoogleOAuthSettings>(googleOAuthSettings);
        services.AddHttpClient<IPublicIpResolver, PublicIpResolver>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddSingleton<IGoogleOAuthService, GoogleOAuthService>();
        services.AddSingleton<ILinuxPreflightService, LinuxPreflightService>();
        services.AddSingleton<IServerLatencyService, PingService>();
        services.AddSingleton<INetworkManagerClient>(sp => new NetworkManagerClient(
            sp.GetRequiredService<IProcessRunner>(),
            File.Exists,
            verifyBrowserDohProtection: true,
            settingsStore: sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<ITunnelTrafficMonitor, TunnelTrafficMonitor>();
        services.AddSingleton<IVpnProfileConverter, OpenVpnProfileConverter>();
        services.AddSingleton<IVpnProfileConverter, IkeV2ProfileConverter>();
        services.AddSingleton<IVpnConnectionService, VpnConnectionService>();
        services.AddSingleton<MainViewModel>();
        services.AddHttpClient("BackendApi", client =>
        {
            client.BaseAddress = backendSettings.BaseUrl;
            client.Timeout = TimeSpan.FromSeconds(45);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LibreGuardLinux/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            MaxResponseHeadersLength = 64
        });
        services.AddSingleton<IBackendApiClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new BackendApiClient(httpClientFactory.CreateClient("BackendApi"));
        });

        return services.BuildServiceProvider();
    }
}

public sealed record AppSettings(string AppVersion)
{
    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var appVersion = document.RootElement
                    .GetProperty("AppVersion")
                    .GetString();

                if (!string.IsNullOrWhiteSpace(appVersion))
                {
                    return new AppSettings(appVersion);
                }
            }
            catch (Exception)
            {
            }
        }

        return new AppSettings("Linux/1.1.17");
    }
}

public sealed record BackendSettings(Uri BaseUrl)
{
    public static BackendSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                var baseUrl = document.RootElement
                    .GetProperty("Backend")
                    .GetProperty("BaseUrl")
                    .GetString();

                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                    && IsTrustedBaseUrl(uri))
                {
                    return new BackendSettings(uri);
                }
            }
            catch (Exception)
            {
            }
        }

        return new BackendSettings(new Uri("https://management.libreguard.net"));
    }

    private static bool IsTrustedBaseUrl(Uri uri)
        => uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.Host.Equals("management.libreguard.net", StringComparison.OrdinalIgnoreCase);
}

public sealed record GoogleOAuthSettings(string? ClientId)
{
    public static GoogleOAuthSettings Load()
        => Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

    public static GoogleOAuthSettings Load(Func<string, string?> getEnvironmentVariable)
        => Load(null, getEnvironmentVariable);

    public static GoogleOAuthSettings Load(string? appSettingsPath, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var desktopClientId = GetFirstEnvironmentValue(getEnvironmentVariable,
            "LIBREGUARD_GOOGLE_DESKTOP_CLIENT_ID",
            "GOOGLE_DESKTOP_CLIENT_ID",
            "GOOGLE_NATIVE_CLIENT_ID",
            "GOOGLE_WINDOWS_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(desktopClientId))
        {
            return new GoogleOAuthSettings(desktopClientId.Trim());
        }

        var configuredOAuth = ResolveGoogleOAuthConfig(
            GetEnvironmentValue(getEnvironmentVariable, "LIBREGUARD_GOOGLE_OAUTH_CONFIG")
            ?? GetEnvironmentValue(getEnvironmentVariable, "GOOGLE_OAUTH_CONFIG"));
        if (!string.IsNullOrWhiteSpace(configuredOAuth))
        {
            return new GoogleOAuthSettings(configuredOAuth.Trim());
        }

        var envClientId = GetFirstEnvironmentValue(getEnvironmentVariable,
            "LIBREGUARD_GOOGLE_CLIENT_ID",
            "Authentication__Google__ClientId",
            "GOOGLE_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(envClientId))
        {
            return new GoogleOAuthSettings(envClientId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(appSettingsPath) && File.Exists(appSettingsPath))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(appSettingsPath));
                var clientId = ExtractAppSettingsClientId(document.RootElement);

                if (!string.IsNullOrWhiteSpace(clientId))
                {
                    return new GoogleOAuthSettings(clientId.Trim());
                }
            }
            catch (Exception)
            {
            }
        }

        return new GoogleOAuthSettings((string?)null);
    }

    private static string? ResolveGoogleOAuthConfig(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (LooksLikeGoogleClientId(trimmed))
        {
            return trimmed;
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(trimmed);
        if (File.Exists(expandedPath))
        {
            try
            {
                return ExtractOAuthClientIdFromJson(File.ReadAllText(expandedPath));
            }
            catch (Exception)
            {
                return null;
            }
        }

        return ExtractOAuthClientIdFromJson(trimmed);
    }

    private static string? ExtractOAuthClientIdFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return ExtractOAuthClientId(document.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ExtractAppSettingsClientId(System.Text.Json.JsonElement root)
        => GetString(root, "OAuth", "GoogleClientId")
           ?? GetString(root, "clientId")
           ?? GetString(root, "client_id");

    private static string? ExtractOAuthClientId(System.Text.Json.JsonElement root)
        => GetString(root, "installed", "client_id")
           ?? GetString(root, "web", "client_id");

    private static string? GetString(System.Text.Json.JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == System.Text.Json.JsonValueKind.String
            ? current.GetString()
            : null;
    }

    private static bool LooksLikeGoogleClientId(string value)
        => value.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static string? GetEnvironmentValue(Func<string, string?> getEnvironmentVariable, string name)
    {
        var value = getEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? GetFirstEnvironmentValue(Func<string, string?> getEnvironmentVariable, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetEnvironmentValue(getEnvironmentVariable, name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}
