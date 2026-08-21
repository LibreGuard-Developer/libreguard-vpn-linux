namespace Libreguard.Vpn.Linux.Services;

public sealed class XdgExternalUriLauncher(IProcessRunner processRunner) : IExternalUriLauncher
{
    public async Task<ExternalUriLaunchResult> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            return new ExternalUriLaunchResult(false, "Only HTTP and HTTPS checkout links can be opened.");
        }

        var result = await processRunner.StartDetachedAsync("xdg-open", [uri.AbsoluteUri], cancellationToken);
        StartupDiagnostics.Log($"external-uri-launch scheme={uri.Scheme} host={uri.Host} exit_code={result.ExitCode}");
        return result.Success
            ? new ExternalUriLaunchResult(true)
            : new ExternalUriLaunchResult(false, "The desktop URL opener failed.");
    }
}
