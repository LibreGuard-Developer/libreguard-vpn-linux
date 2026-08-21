using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace Libreguard.Vpn.Linux.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard is not { } clipboard)
        {
            throw new InvalidOperationException("Clipboard is not available.");
        }

        await clipboard.SetTextAsync(text);
    }
}
