using Libreguard.Vpn.Linux;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class ProgramDiagnosticsTests
{
    [Fact]
    public void BuildStartupDiagnostics_IncludesEnvironmentAndRuntimeData()
    {
        var diagnostics = Program.BuildStartupDiagnostics(new Dictionary<string, string?>
        {
            ["DISPLAY"] = ":0",
            ["XDG_SESSION_TYPE"] = "x11",
            ["WAYLAND_DISPLAY"] = null,
            ["HOME"] = "/home/test"
        });

        Assert.Contains("DISPLAY=:0", diagnostics);
        Assert.Contains("XDG_SESSION_TYPE=x11", diagnostics);
        Assert.Contains("Runtime=", diagnostics);
        Assert.Contains("HOME=/home/test", diagnostics);
        Assert.Contains("StartupLogPath=", diagnostics);
    }
}
