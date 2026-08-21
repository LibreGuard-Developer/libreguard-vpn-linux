namespace Libreguard.Vpn.Linux.Services;

public static class XdgPaths
{
    internal const string VpnCredentialDirectoryEnvironmentVariable = "LIBREGUARD_VPN_CREDENTIAL_DIR";

    public static string ConfigHome => Resolve("XDG_CONFIG_HOME", ".config");
    public static string StateHome => Resolve("XDG_STATE_HOME", ".local/state");
    public static string CacheHome => Resolve("XDG_CACHE_HOME", ".cache");

    public static string AppConfigDirectory => Path.Combine(ConfigHome, "libreguard");
    public static string AppStateDirectory => Path.Combine(StateHome, "libreguard");
    public static string LegacyVpnConfigDirectory => Path.Combine(AppStateDirectory, "configs");
    public static string NewerVpnCredentialDirectory => ResolveNewerVpnCredentialDirectory(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public static string IkeV2CredentialDirectory => ResolveIkeV2CredentialDirectory(
        Environment.GetEnvironmentVariable(VpnCredentialDirectoryEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    public static string VpnCredentialDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(VpnCredentialDirectoryEnvironmentVariable);
            return string.IsNullOrWhiteSpace(overridePath)
                ? LegacyVpnConfigDirectory
                : ResolveVpnCredentialDirectory(overridePath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }
    [Obsolete("Use VpnCredentialDirectory. This compatibility alias now resolves to the active VPN credential directory.")]
    public static string ConfigDirectory => VpnCredentialDirectory;
    public static string DownloadsDirectory => Path.Combine(AppStateDirectory, "downloads");
    public static string WebViewDataDirectory => Path.Combine(AppStateDirectory, "webview");
    public static string WebViewCacheDirectory => Path.Combine(CacheHome, "libreguard", "webview");
    public static string StartupLogFilePath => Path.Combine(AppStateDirectory, "startup.log");

    private static string Resolve(string environmentVariable, string fallback)
    {
        var explicitPath = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, fallback);
    }

    public static void EnsureAppDirectories()
    {
        FileSecurity.EnsurePrivateDirectory(AppConfigDirectory);
        FileSecurity.EnsurePrivateDirectory(AppStateDirectory);
        FileSecurity.EnsurePrivateDirectory(DownloadsDirectory);
    }

    internal static void EnsureVpnCredentialDirectory()
        => EnsurePrivateVpnCredentialDirectory(VpnCredentialDirectory);

    internal static void EnsureIkeV2CredentialDirectory()
        => EnsurePrivateVpnCredentialDirectory(IkeV2CredentialDirectory);

    private static void EnsurePrivateVpnCredentialDirectory(string credentialDirectory)
    {
        var overridePath = Environment.GetEnvironmentVariable(VpnCredentialDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            var parent = Path.GetDirectoryName(credentialDirectory);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                FileSecurity.EnsurePrivateDirectory(parent);
            }
        }

        FileSecurity.EnsurePrivateDirectory(credentialDirectory);
    }

    internal static string ResolveVpnCredentialDirectory(string? overridePath, string? userProfile)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath.Trim()));
        }

        var home = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetEnvironmentVariable("HOME")
            : userProfile;
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("The current user's home directory could not be resolved.");
        }

        return Path.Combine(home, ".local", "state", "libreguard", "configs");
    }

    internal static string ResolveNewerVpnCredentialDirectory(string? userProfile)
    {
        var home = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetEnvironmentVariable("HOME")
            : userProfile;
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("The current user's home directory could not be resolved.");
        }

        return Path.Combine(home, ".cert", "libreguard");
    }

    internal static string ResolveIkeV2CredentialDirectory(string? overridePath, string? userProfile)
        => string.IsNullOrWhiteSpace(overridePath)
            ? ResolveNewerVpnCredentialDirectory(userProfile)
            : ResolveVpnCredentialDirectory(overridePath, userProfile);
}
