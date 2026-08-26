using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Libreguard.Vpn.Linux.Services;

public sealed class SecretServiceStore : ISecretStore
{
    private const string AttributeName = "libreguard-key";
    private const int MaxCapturedOutputChars = 64 * 1024;

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var result = await RunSecretToolAsync(
            ["lookup", AttributeName, key],
            input: null,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                throw new InvalidOperationException("Secret Service is unavailable.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        return result.StandardOutput.TrimEnd('\r', '\n');
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var result = await RunSecretToolAsync(
            ["store", "--label", $"LibreGuard VPN {key}", AttributeName, key],
            value,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Secret Service is unavailable.");
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var result = await RunSecretToolAsync(["clear", AttributeName, key], input: null, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Secret Service could not delete the requested secret.");
        }
    }

    private static async Task<ProcessResult> RunSecretToolAsync(IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
    {
        var executable = OperatingSystem.IsLinux() ? "/usr/bin/secret-tool" : "secret-tool";
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessResult(127, string.Empty, "The Secret Service process could not be started.");
            }

            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var errorTask = ReadBoundedAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(127, string.Empty, "The Secret Service process could not be started.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (builder.Length < MaxCapturedOutputChars)
            {
                var count = Math.Min(read, MaxCapturedOutputChars - builder.Length);
                builder.Append(buffer, 0, count);
            }
        }

        return builder.ToString();
    }
}

public sealed class FileFallbackSecretStore : ISecretStore
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SharedPermissionBits =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private readonly string _filePath = Path.Combine(XdgPaths.AppConfigDirectory, "dev-secrets.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool HasPersistedFallbackSelection
    {
        get
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return false;
                }

                FileSecurity.EnsureNotSymbolicLink(_filePath);
                using var stream = File.OpenRead(_filePath);
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                return values is not null
                    && values.TryGetValue(CompositeSecretStore.FallbackMarkerKey, out var backend)
                    && string.Equals(backend, CompositeSecretStore.FallbackMarkerValue, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                // Let the normal read path produce the actionable security or
                // permission error instead of probing Secret Service first.
                return true;
            }
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAllAsync(cancellationToken);
            return values.TryGetValue(key, out var value) ? value : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAllAsync(cancellationToken);
            values[key] = value;
            await WriteAllAsync(values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAllAsync(cancellationToken);
            values.Remove(key);
            await WriteAllAsync(values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        XdgPaths.EnsureAppDirectories();
        if (!File.Exists(_filePath))
        {
            return [];
        }

        EnsurePrivateExistingFile(_filePath);
        FileSecurity.EnsureNotSymbolicLink(_filePath);
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    private async Task WriteAllAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        XdgPaths.EnsureAppDirectories();
        EnsurePrivateSecretDirectory();

        var directory = Path.GetDirectoryName(_filePath) ?? XdgPaths.AppConfigDirectory;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = CreatePrivateFileStream(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, values, JsonOptions.Pretty, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            EnsurePrivateExistingFile(tempPath);
            FileSecurity.EnsureNotSymbolicLink(_filePath);
            File.Move(tempPath, _filePath, overwrite: true);
            EnsurePrivateExistingFile(_filePath);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static FileStream CreatePrivateFileStream(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.None,
            UnixCreateMode = PrivateFileMode
        });
    }

    private static void EnsurePrivateSecretDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            FileSecurity.EnsureNotSymbolicLink(XdgPaths.AppConfigDirectory);
            File.SetUnixFileMode(XdgPaths.AppConfigDirectory, PrivateDirectoryMode);
            var mode = File.GetUnixFileMode(XdgPaths.AppConfigDirectory);
            if ((mode & SharedPermissionBits) != 0
                || !mode.HasFlag(UnixFileMode.UserRead)
                || !mode.HasFlag(UnixFileMode.UserWrite)
                || !mode.HasFlag(UnixFileMode.UserExecute))
            {
                throw new InvalidOperationException($"Fallback secret directory '{XdgPaths.AppConfigDirectory}' is not private.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"Could not secure fallback secret directory '{XdgPaths.AppConfigDirectory}'.", ex);
        }
    }

    private static void EnsurePrivateExistingFile(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            FileSecurity.EnsureNotSymbolicLink(path);
            File.SetUnixFileMode(path, PrivateFileMode);
            var mode = File.GetUnixFileMode(path);
            if ((mode & SharedPermissionBits) != 0
                || !mode.HasFlag(UnixFileMode.UserRead)
                || !mode.HasFlag(UnixFileMode.UserWrite))
            {
                throw new InvalidOperationException($"Fallback secret file '{path}' is not private.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"Could not secure fallback secret file '{path}'.", ex);
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class CompositeSecretStore : ISecretStore
{
    internal const string FallbackMarkerKey = "__libreguard-secret-store-backend-v1";
    internal const string FallbackMarkerValue = "file";

    private readonly ISecretStore _primary;
    private readonly ISecretStore _fallback;
    private readonly Action<string> _diagnosticSink;
    private int _useFallback;

    public CompositeSecretStore(
        ISecretStore primary,
        ISecretStore fallback,
        bool preferFallback = false,
        Action<string>? diagnosticSink = null)
    {
        _primary = primary;
        _fallback = fallback;
        _diagnosticSink = diagnosticSink ?? (_ => { });
        _useFallback = preferFallback ? 1 : 0;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (UsesFallback)
        {
            return await _fallback.GetAsync(key, cancellationToken);
        }

        try
        {
            var value = await _primary.GetAsync(key, cancellationToken);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        catch (InvalidOperationException)
        {
            await SwitchToFallbackAsync(cancellationToken);
        }

        return await _fallback.GetAsync(key, cancellationToken);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (UsesFallback)
        {
            await _fallback.SetAsync(key, value, cancellationToken);
            return;
        }

        try
        {
            await _primary.SetAsync(key, value, cancellationToken);
            return;
        }
        catch (InvalidOperationException)
        {
            await SwitchToFallbackAsync(cancellationToken);
            await _fallback.SetAsync(key, value, cancellationToken);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        try
        {
            await _primary.DeleteAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            firstFailure = ex;
            if (!UsesFallback)
            {
                await SwitchToFallbackAsync(cancellationToken);
            }
        }

        try
        {
            await _fallback.DeleteAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "One or more secret stores could not delete the requested secret.",
                firstFailure ?? ex);
        }

        if (firstFailure is not null)
        {
            // Reads and writes deliberately honor the persisted fallback choice,
            // but deletion is different: a session may have been stored in
            // Secret Service before the fallback was selected. Do not report a
            // successful logout while that primary copy could not be removed.
            throw new InvalidOperationException(
                "The primary secret store could not delete the requested secret.",
                firstFailure);
        }
    }

    private bool UsesFallback => Volatile.Read(ref _useFallback) != 0;

    private async Task SwitchToFallbackAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _useFallback, 1) != 0)
        {
            return;
        }

        _diagnosticSink("secret-store-backend backend=file reason=secret-service-unavailable");
        try
        {
            // Persist the backend choice. On later launches this prevents a
            // locked, missing, or broken desktop keyring from prompting again.
            await _fallback.SetAsync(FallbackMarkerKey, FallbackMarkerValue, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _diagnosticSink($"secret-store-backend-marker-failed type={ex.GetType().Name}");
        }
    }
}

public sealed class LocalSettingsStore : ISettingsStore
{
    private readonly string _filePath = Path.Combine(XdgPaths.AppConfigDirectory, "settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAllAsync(cancellationToken);
            if (!values.TryGetValue(key, out var element))
            {
                return default;
            }

            return element.Deserialize<T>(JsonOptions.Default);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAllAsync(cancellationToken);
            values[key] = JsonSerializer.SerializeToElement(value, JsonOptions.Default);
            await WriteAllAsync(values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, JsonElement>> ReadAllAsync(CancellationToken cancellationToken)
    {
        XdgPaths.EnsureAppDirectories();
        if (!File.Exists(_filePath))
        {
            return [];
        }

        FileSecurity.EnsureNotSymbolicLink(_filePath);
        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(stream, JsonOptions.Default, cancellationToken) ?? [];
    }

    private async Task WriteAllAsync(Dictionary<string, JsonElement> values, CancellationToken cancellationToken)
    {
        XdgPaths.EnsureAppDirectories();
        FileSecurity.EnsurePrivateDirectory(XdgPaths.AppConfigDirectory);
        var directory = Path.GetDirectoryName(_filePath) ?? XdgPaths.AppConfigDirectory;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = FileSecurity.CreatePrivateFile(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, values, JsonOptions.Pretty, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            FileSecurity.EnsurePrivateFile(tempPath);
            FileSecurity.EnsureNotSymbolicLink(_filePath);
            File.Move(tempPath, _filePath, overwrite: true);
            FileSecurity.EnsurePrivateFile(_filePath);
        }
        catch
        {
            FileSecurity.TryDelete(tempPath);
            throw;
        }
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
