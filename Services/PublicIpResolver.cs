using System.Net;

namespace Libreguard.Vpn.Linux.Services;

public sealed class PublicIpResolver(HttpClient httpClient) : IPublicIpResolver
{
    private static readonly string[] Endpoints =
    [
        "https://api.ipify.org",
        "https://icanhazip.com",
        "https://ifconfig.me/ip",
        "https://checkip.amazonaws.com"
    ];

    public async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var response = await httpClient.GetAsync(endpoint, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (IPAddress.TryParse(body, out var parsed))
                {
                    return parsed.ToString();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return null;
    }
}
