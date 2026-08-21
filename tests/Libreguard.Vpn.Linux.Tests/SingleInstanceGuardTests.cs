using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_AllowsOnlyOneOwnerAndReleasesOnDispose()
    {
        var lockPath = Path.Combine(Path.GetTempPath(), $"libreguard-{Guid.NewGuid():N}.lock");

        try
        {
            var first = SingleInstanceGuard.TryAcquire(lockPath);
            Assert.Equal(SingleInstanceAcquireStatus.Acquired, first.Status);
            Assert.NotNull(first.Guard);

            var second = SingleInstanceGuard.TryAcquire(lockPath);
            Assert.Equal(SingleInstanceAcquireStatus.AlreadyRunning, second.Status);
            Assert.Null(second.Guard);

            first.Guard!.Dispose();

            var third = SingleInstanceGuard.TryAcquire(lockPath);
            Assert.Equal(SingleInstanceAcquireStatus.Acquired, third.Status);
            third.Guard!.Dispose();
        }
        finally
        {
            File.Delete(lockPath);
        }
    }

    [Fact]
    public void GetDefaultLockPath_UsesPerUserRuntimeDirectoryWhenAvailable()
    {
        var originalRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var runtimeDirectory = Path.Combine(Path.GetTempPath(), $"libreguard-runtime-{Guid.NewGuid():N}");

        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDirectory);

            var lockPath = SingleInstanceGuard.GetDefaultLockPath();

            Assert.Equal(Path.Combine(runtimeDirectory, "libreguard-vpn-linux.lock"), lockPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", originalRuntimeDirectory);
        }
    }
}
