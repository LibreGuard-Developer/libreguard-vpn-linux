using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class StartupDiagnosticsTests
{
    [Fact]
    public void StartupLogPath_UsesXdgStateDirectory()
    {
        var expected = Path.Combine(XdgPaths.AppStateDirectory, "startup.log");

        Assert.Equal(expected, StartupDiagnostics.StartupLogPath);
    }
}
