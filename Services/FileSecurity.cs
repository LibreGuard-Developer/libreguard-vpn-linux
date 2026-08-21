using System.Text;

namespace Libreguard.Vpn.Linux.Services;

internal static class FileSecurity
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SharedPermissionBits =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    public static void EnsurePrivateDirectory(string path)
    {
        EnsureNotSymbolicLink(path);
        Directory.CreateDirectory(path);
        EnsureNotSymbolicLink(path);

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
            var mode = File.GetUnixFileMode(path);
            if ((mode & SharedPermissionBits) != 0
                || !mode.HasFlag(UnixFileMode.UserRead)
                || !mode.HasFlag(UnixFileMode.UserWrite)
                || !mode.HasFlag(UnixFileMode.UserExecute))
            {
                throw new InvalidOperationException($"Directory '{path}' is not private.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"Could not secure directory '{path}'.", ex);
        }
    }

    public static void EnsurePrivateFile(string path)
    {
        EnsureNotSymbolicLink(path);

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, PrivateFileMode);
            var mode = File.GetUnixFileMode(path);
            if ((mode & SharedPermissionBits) != 0
                || !mode.HasFlag(UnixFileMode.UserRead)
                || !mode.HasFlag(UnixFileMode.UserWrite))
            {
                throw new InvalidOperationException($"File '{path}' is not private.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"Could not secure file '{path}'.", ex);
        }
    }

    public static FileStream CreatePrivateFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            EnsurePrivateDirectory(directory);
        }

        if (!OperatingSystem.IsLinux())
        {
            return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = PrivateFileMode
        });
    }

    public static void AppendPrivateText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            EnsurePrivateDirectory(directory);
        }

        EnsureNotSymbolicLink(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous
        };
        if (OperatingSystem.IsLinux())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        using var stream = new FileStream(path, options);
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        writer.Write(text);
        writer.Flush();
        stream.Flush(flushToDisk: false);
        EnsurePrivateFile(path);
    }

    public static void EnsureNotSymbolicLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidOperationException($"Refusing to use symbolic link '{path}'.");
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                EnsureNotSymbolicLink(path);
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
