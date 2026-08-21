namespace Libreguard.Vpn.Linux.Services;

public sealed class FreedesktopNotificationService(IProcessRunner processRunner) : IDesktopNotificationService
{
    public async Task ShowAsync(string title, string body, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        try
        {
            await processRunner.RunAsync(
                "gdbus",
                [
                    "call",
                    "--session",
                    "--dest",
                    "org.freedesktop.Notifications",
                    "--object-path",
                    "/org/freedesktop/Notifications",
                    "--method",
                    "org.freedesktop.Notifications.Notify",
                    ToVariantString("LibreGuard VPN"),
                    "0",
                    "''",
                    ToVariantString(title),
                    ToVariantString(body),
                    "[]",
                    "{}",
                    "5000"
                ],
                cancellationToken);
        }
        catch
        {
            // Desktop notification support depends on the user's session daemon.
        }
    }

    private static string ToVariantString(string value)
        => $"'{value.Replace("'", "\\'", StringComparison.Ordinal)}'";
}
