using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Libreguard.Vpn.Linux.Services;

public sealed class GoogleOAuthService : IGoogleOAuthService
{
    private readonly GoogleOAuthSettings _settings;
    private readonly IProcessRunner _processRunner;

    public GoogleOAuthService(GoogleOAuthSettings settings, IProcessRunner processRunner)
    {
        _settings = settings;
        _processRunner = processRunner;
    }

    public async Task<GoogleOAuthAuthorizationCode> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            throw new InvalidOperationException("Google OAuth client ID is not configured. Set LIBREGUARD_GOOGLE_OAUTH_CONFIG, LIBREGUARD_GOOGLE_DESKTOP_CLIENT_ID, GOOGLE_DESKTOP_CLIENT_ID, GOOGLE_NATIVE_CLIENT_ID, GOOGLE_WINDOWS_CLIENT_ID, or an explicit generic override such as LIBREGUARD_GOOGLE_CLIENT_ID before building, or place GoogleClientId under OAuth in appsettings.json.");
        }

        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateToken();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        var authUrl = BuildAuthorizationUrl(_settings.ClientId, redirectUri, state, codeChallenge);

        var browser = await _processRunner.StartDetachedAsync("xdg-open", [authUrl], cancellationToken);
        if (!browser.Success)
        {
            throw new InvalidOperationException("Unable to launch the system browser for Google sign-in.");
        }

        var callback = await WaitForCallbackAsync(listener, cancellationToken);
        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Google sign-in was cancelled or returned an invalid state.");
        }

        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            throw new InvalidOperationException("Google sign-in failed. Please try again.");
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            throw new InvalidOperationException("Google sign-in did not return an authorization code.");
        }

        return new GoogleOAuthAuthorizationCode(_settings.ClientId.Trim(), callback.Code, redirectUri, codeVerifier);
    }

    private static string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
            ["include_granted_scopes"] = "true"
        };

        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query.Select(pair =>
            $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));
    }

    private static async Task<GoogleCallback> WaitForCallbackAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var callback = await TryReadCallbackAsync(stream, timeout.Token);
            if (callback is not null)
            {
                return callback;
            }
        }
    }

    private static async Task<GoogleCallback?> TryReadCallbackAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const int maxRequestHeadBytes = 32 * 1024;
        const int maxRequestTargetLength = 8 * 1024;
        var requestBytes = new MemoryStream();
        var buffer = new byte[4096];
        var headerTerminated = false;

        while (requestBytes.Length < maxRequestHeadBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            requestBytes.Write(buffer, 0, read);
            if (ContainsHeaderTerminator(requestBytes.GetBuffer(), checked((int)requestBytes.Length)))
            {
                headerTerminated = true;
                break;
            }
        }

        if (!headerTerminated)
        {
            await WriteIgnoredRequestResponseAsync(stream, cancellationToken);
            return null;
        }

        var requestHead = Encoding.ASCII.GetString(requestBytes.ToArray());
        var requestLine = requestHead.Split("\r\n", 2, StringSplitOptions.None)[0];
        var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 3
            || !string.Equals(requestParts[0], "GET", StringComparison.Ordinal)
            || requestParts[1].Length > maxRequestTargetLength)
        {
            await WriteIgnoredRequestResponseAsync(stream, cancellationToken);
            return null;
        }

        var requestTarget = requestParts[1];
        if (!Uri.TryCreate($"http://127.0.0.1{requestTarget}", UriKind.Absolute, out var uri)
            || !string.Equals(uri.AbsolutePath, "/callback", StringComparison.Ordinal))
        {
            await WriteIgnoredRequestResponseAsync(stream, cancellationToken);
            return null;
        }

        var query = ParseQuery(uri.Query);

        var callback = new GoogleCallback(
            query.TryGetValue("code", out var code) ? code : null,
            query.TryGetValue("state", out var state) ? state : null,
            query.TryGetValue("error", out var error) ? error : null);

        await WriteBrowserResponseAsync(stream, callback, cancellationToken);
        return callback;
    }

    private static bool ContainsHeaderTerminator(byte[] buffer, int length)
    {
        for (var index = 3; index < length; index++)
        {
            if (buffer[index - 3] == '\r'
                && buffer[index - 2] == '\n'
                && buffer[index - 1] == '\r'
                && buffer[index] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteIgnoredRequestResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const string body = """
                            <!doctype html>
                            <html>
                              <body>
                                <h1>LibreGuard VPN sign-in</h1>
                                <p>This local listener is waiting for Google sign-in to complete.</p>
                              </body>
                            </html>
                            """;

        var response = $"HTTP/1.1 404 Not Found\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteBrowserResponseAsync(NetworkStream stream, GoogleCallback callback, CancellationToken cancellationToken)
    {
        var body = callback.Error is null
            ? """
              <!doctype html>
              <html>
                <body>
                  <h1>Sign-in complete</h1>
                  <p>You can return to LibreGuard VPN now.</p>
                </body>
              </html>
              """
            : $"""
               <!doctype html>
               <html>
                 <body>
                   <h1>Sign-in failed</h1>
                   <p>{WebUtility.HtmlEncode(callback.Error)}</p>
                 </body>
               </html>
               """;

        var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0]);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) ?? string.Empty : string.Empty;
        }

        return result;
    }

    private static string GenerateCodeVerifier() => GenerateToken();

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record GoogleCallback(string? Code, string? State, string? Error);
}
