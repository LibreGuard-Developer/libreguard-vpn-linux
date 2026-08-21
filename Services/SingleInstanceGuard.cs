using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Libreguard.Vpn.Linux.Services;

internal enum SingleInstanceAcquireStatus
{
    Acquired,
    AlreadyRunning,
    Failed
}

internal readonly record struct SingleInstanceAcquireResult(
    SingleInstanceAcquireStatus Status,
    SingleInstanceGuard? Guard,
    string LockPath,
    string? ErrorMessage);

internal sealed class SingleInstanceGuard : IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private const int ErrorWouldBlock = 11;
    private const int ErrorAgain = 35;

    private readonly FileStream _lockFile;
    private int _disposed;

    private SingleInstanceGuard(FileStream lockFile, string lockPath)
    {
        _lockFile = lockFile;
        LockPath = lockPath;
    }

    internal string LockPath { get; }

    internal static SingleInstanceAcquireResult TryAcquire(string? lockPath = null)
    {
        string resolvedLockPath;
        try
        {
            resolvedLockPath = Path.GetFullPath(lockPath ?? GetDefaultLockPath());
        }
        catch (Exception ex)
        {
            return Failed(string.Empty, $"Could not determine the single-instance lock path: {ex.Message}");
        }

        FileStream? lockFile = null;
        try
        {
            var directory = Path.GetDirectoryName(resolvedLockPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failed(resolvedLockPath, "The single-instance lock path has no parent directory.");
            }

            Directory.CreateDirectory(directory);
            lockFile = new FileStream(
                resolvedLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            var lockResult = TryLock(lockFile);
            if (lockResult.Status == SingleInstanceAcquireStatus.Acquired)
            {
                return new(lockResult.Status, new SingleInstanceGuard(lockFile, resolvedLockPath), resolvedLockPath, null);
            }

            lockFile.Dispose();
            lockFile = null;
            return new(lockResult.Status, null, resolvedLockPath, lockResult.ErrorMessage);
        }
        catch (Exception ex)
        {
            lockFile?.Dispose();
            return Failed(resolvedLockPath, $"Could not establish the single-instance lock: {ex.Message}");
        }
    }

    internal static string GetDefaultLockPath()
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory))
        {
            return Path.Combine(runtimeDirectory, "libreguard-vpn-linux.lock");
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            var homeDirectory = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(homeDirectory) && Path.IsPathFullyQualified(homeDirectory))
            {
                localApplicationData = Path.Combine(homeDirectory, ".local", "share");
            }
        }

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("No per-user runtime directory is available.");
        }

        return Path.Combine(localApplicationData, "LibreGuard", "libreguard-vpn-linux.lock");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                _ = flock(GetFileDescriptor(_lockFile.SafeFileHandle), LockUnlock);
            }
            else if (OperatingSystem.IsWindows())
            {
                _lockFile.Unlock(0, 1);
            }
        }
        catch
        {
            // Closing the file also releases the lock, including during process exit.
        }
        finally
        {
            _lockFile.Dispose();
        }
    }

    private static SingleInstanceAcquireResult TryLock(FileStream lockFile)
    {
        if (OperatingSystem.IsLinux())
        {
            var result = flock(GetFileDescriptor(lockFile.SafeFileHandle), LockExclusive | LockNonBlocking);
            if (result == 0)
            {
                return new(SingleInstanceAcquireStatus.Acquired, null, string.Empty, null);
            }

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorWouldBlock or ErrorAgain)
            {
                return new(SingleInstanceAcquireStatus.AlreadyRunning, null, string.Empty, null);
            }

            return new(
                SingleInstanceAcquireStatus.Failed,
                null,
                string.Empty,
                $"The operating system could not lock the file (errno {error}).");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new(
                SingleInstanceAcquireStatus.Failed,
                null,
                string.Empty,
                "Single-instance locking is not supported on this operating system.");
        }

        try
        {
            lockFile.Lock(0, 1);
            return new(SingleInstanceAcquireStatus.Acquired, null, string.Empty, null);
        }
        catch (IOException)
        {
            return new(SingleInstanceAcquireStatus.AlreadyRunning, null, string.Empty, null);
        }
    }

    private static SingleInstanceAcquireResult Failed(string lockPath, string errorMessage)
        => new(SingleInstanceAcquireStatus.Failed, null, lockPath, errorMessage);

    private static int GetFileDescriptor(SafeFileHandle handle)
        => checked((int)handle.DangerousGetHandle().ToInt64());

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int flock(int fileDescriptor, int operation);
}
