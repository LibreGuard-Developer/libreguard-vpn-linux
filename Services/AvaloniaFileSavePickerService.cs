using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Libreguard.Vpn.Linux.Services;

public sealed class AvaloniaFileSavePickerService : IFileSavePickerService
{
    public Window? Owner { get; set; }

    public async Task<FileSaveTarget?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        var safeName = SanitizeFileName(suggestedFileName);
        if (Owner?.StorageProvider.CanSave == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await Owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save LibreGuard certificate file",
                SuggestedFileName = safeName,
                DefaultExtension = NormalizeExtension(Path.GetExtension(safeName)),
                ShowOverwritePrompt = true
            });

            if (file is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stream = await file.OpenWriteAsync();
            return new FileSaveTarget(stream, file.TryGetLocalPath() ?? file.Name);
        }

        XdgPaths.EnsureAppDirectories();
        var fallbackPath = Path.Combine(XdgPaths.DownloadsDirectory, safeName);
        return new FileSaveTarget(File.Create(fallbackPath), fallbackPath);
    }

    private static string? NormalizeExtension(string extension)
        => string.IsNullOrWhiteSpace(extension)
            ? null
            : extension.TrimStart('.');

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fileName.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        return new string(chars);
    }
}
