using System.Text.Json;

namespace Libreguard.Vpn.Linux.Services;

internal static class BuildIdentity
{
    private const string FileName = "build-info.json";

    public static void Log()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                StartupDiagnostics.Log("build-identity unavailable");
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var version = GetString(root, "version");
            var buildId = GetString(root, "buildId");
            var revision = GetString(root, "gitRevision");
            if (revision.Length > 12) revision = revision[..12];
            var dirty = root.TryGetProperty("dirty", out var dirtyElement) && dirtyElement.ValueKind == JsonValueKind.True;
            StartupDiagnostics.Log(
                $"build-identity version={Sanitize(version)} build_id={Sanitize(buildId)} " +
                $"revision={Sanitize(revision)} dirty={dirty.ToString().ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log($"build-identity-error type={ex.GetType().Name}");
        }
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) ? value.GetString() ?? "unknown" : "unknown";

    private static string Sanitize(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Replace(' ', '-').Replace('"', '\'');
}
