using System.Net.Sockets;
using System.Text;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class GoogleOAuthServiceTests
{
    [Fact]
    public void Load_PrefersEnvironmentClientIdOverAppSettings()
    {
        var path = CreateTempAppSettings("""
        {
          "OAuth": {
            "GoogleClientId": "appsettings-client-id"
          }
        }
        """);

        try
        {
            var settings = GoogleOAuthSettings.Load(path, key => key == "LIBREGUARD_GOOGLE_CLIENT_ID" ? " env-client-id " : null);

            Assert.Equal("env-client-id", settings.ClientId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("LIBREGUARD_GOOGLE_DESKTOP_CLIENT_ID")]
    [InlineData("GOOGLE_WINDOWS_CLIENT_ID")]
    [InlineData("Authentication__Google__ClientId")]
    [InlineData("GOOGLE_CLIENT_ID")]
    [InlineData("GOOGLE_DESKTOP_CLIENT_ID")]
    [InlineData("GOOGLE_NATIVE_CLIENT_ID")]
    public void Load_UsesSupportedEnvironmentAliases(string variableName)
    {
        var settings = GoogleOAuthSettings.Load(key => key == variableName ? $" {variableName}-value " : null);

        Assert.Equal($"{variableName}-value", settings.ClientId);
    }

    [Fact]
    public void Load_DoesNotUseWebOrAndroidClientIdsForLinuxDesktop()
    {
        var settings = GoogleOAuthSettings.Load(key => key switch
        {
            "GOOGLE_WEB_CLIENT_ID" => "web-client-id.apps.googleusercontent.com",
            "GOOGLE_ANDROID_CLIENT_ID" => "android-client-id.apps.googleusercontent.com",
            _ => null
        });

        Assert.Null(settings.ClientId);
    }

    [Fact]
    public void Load_PrefersDesktopClientIdOverGenericClientId()
    {
        var settings = GoogleOAuthSettings.Load(key => key switch
        {
            "LIBREGUARD_GOOGLE_DESKTOP_CLIENT_ID" => " desktop-client-id.apps.googleusercontent.com ",
            "LIBREGUARD_GOOGLE_CLIENT_ID" => "generic-client-id.apps.googleusercontent.com",
            "GOOGLE_CLIENT_ID" => "google-client-id",
            _ => null
        });

        Assert.Equal("desktop-client-id.apps.googleusercontent.com", settings.ClientId);
    }

    [Fact]
    public void Load_UsesOAuthConfigWhenItIsClientId()
    {
        var settings = GoogleOAuthSettings.Load(key => key == "LIBREGUARD_GOOGLE_OAUTH_CONFIG"
            ? " oauth-config-client-id.apps.googleusercontent.com "
            : null);

        Assert.Equal("oauth-config-client-id.apps.googleusercontent.com", settings.ClientId);
    }

    [Fact]
    public void Load_PrefersDesktopClientIdOverOAuthConfig()
    {
        var settings = GoogleOAuthSettings.Load(key => key switch
        {
            "GOOGLE_DESKTOP_CLIENT_ID" => "desktop-client-id.apps.googleusercontent.com",
            "LIBREGUARD_GOOGLE_OAUTH_CONFIG" => "oauth-config-client-id.apps.googleusercontent.com",
            _ => null
        });

        Assert.Equal("desktop-client-id.apps.googleusercontent.com", settings.ClientId);
    }

    [Fact]
    public void Load_ExtractsInstalledClientIdFromOAuthConfigJson()
    {
        var settings = GoogleOAuthSettings.Load(key => key == "LIBREGUARD_GOOGLE_OAUTH_CONFIG"
            ? """
              {
                "installed": {
                  "client_id": "installed-client-id.apps.googleusercontent.com",
                  "client_secret": "do-not-use"
                }
              }
              """
            : null);

        Assert.Equal("installed-client-id.apps.googleusercontent.com", settings.ClientId);
    }

    [Fact]
    public void Load_ExtractsWebClientIdFromOAuthConfigJson()
    {
        var settings = GoogleOAuthSettings.Load(key => key == "GOOGLE_OAUTH_CONFIG"
            ? """
              {
                "web": {
                  "client_id": "web-client-id.apps.googleusercontent.com",
                  "client_secret": "do-not-use"
                }
              }
              """
            : null);

        Assert.Equal("web-client-id.apps.googleusercontent.com", settings.ClientId);
    }

    [Fact]
    public void Load_ExtractsClientIdFromOAuthConfigPath()
    {
        var path = CreateTempAppSettings("""
        {
          "installed": {
            "client_id": "path-client-id.apps.googleusercontent.com",
            "client_secret": "do-not-use"
          }
        }
        """);

        try
        {
            var settings = GoogleOAuthSettings.Load(key => key == "LIBREGUARD_GOOGLE_OAUTH_CONFIG" ? path : null);

            Assert.Equal("path-client-id.apps.googleusercontent.com", settings.ClientId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ExtractsWebClientIdFromOAuthConfigPath()
    {
        var path = CreateTempAppSettings("""
        {
          "web": {
            "client_id": "path-web-client-id.apps.googleusercontent.com",
            "client_secret": "do-not-use"
          }
        }
        """);

        try
        {
            var settings = GoogleOAuthSettings.Load(key => key == "LIBREGUARD_GOOGLE_OAUTH_CONFIG" ? path : null);

            Assert.Equal("path-web-client-id.apps.googleusercontent.com", settings.ClientId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UsesAppSettingsOAuthClientIdWhenEnvironmentIsMissing()
    {
        var path = CreateTempAppSettings("""
        {
          "OAuth": {
            "GoogleClientId": "appsettings-client-id"
          }
        }
        """);

        try
        {
            var settings = GoogleOAuthSettings.Load(path, _ => null);

            Assert.Equal("appsettings-client-id", settings.ClientId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReturnsNullWhenUnconfigured()
    {
        var settings = GoogleOAuthSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"), _ => null);

        Assert.Null(settings.ClientId);
    }

    [Fact]
    public async Task AuthenticateAsync_ThrowsHelpfulMessageWhenClientIdIsMissing()
    {
        var service = CreateService(null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthenticateAsync(CancellationToken.None));

        Assert.Contains("LIBREGUARD_GOOGLE_DESKTOP_CLIENT_ID", ex.Message);
        Assert.Contains("GOOGLE_DESKTOP_CLIENT_ID", ex.Message);
        Assert.Contains("LIBREGUARD_GOOGLE_CLIENT_ID", ex.Message);
        Assert.Contains("appsettings.json", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_ThrowsOnStateMismatch()
    {
        var runner = new RecordingProcessRunner();
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        await Task.Delay(50);
        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["code"] = "authorization-code",
            ["state"] = "wrong-state"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => authTask);

        Assert.Contains("invalid state", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticateAsync_UsesDetachedBrowserLaunchBeforeWaitingForCallback()
    {
        var runner = new RecordingProcessRunner
        {
            RunAsyncTask = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task
        };
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["code"] = "authorization-code",
            ["state"] = authQuery["state"]
        });

        var authorizationCode = await authTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("authorization-code", authorizationCode.Code);
        Assert.True(runner.StartDetachedAsyncCalled);
        Assert.False(runner.RunAsyncCalled);
    }

    [Fact]
    public async Task AuthenticateAsync_IgnoresRequestsBeforeCallback()
    {
        var runner = new RecordingProcessRunner();
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        await SendRawRequestAsync(authQuery["redirect_uri"], "/");
        await SendRawRequestAsync(authQuery["redirect_uri"], "/favicon.ico");
        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["code"] = "authorization-code",
            ["state"] = authQuery["state"]
        });

        var authorizationCode = await authTask;

        Assert.Equal("authorization-code", authorizationCode.Code);
    }

    [Fact]
    public async Task AuthenticateAsync_IgnoresEmptyConnectionBeforeCallback()
    {
        var runner = new RecordingProcessRunner();
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        await OpenAndCloseConnectionAsync(authQuery["redirect_uri"]);
        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["code"] = "authorization-code",
            ["state"] = authQuery["state"]
        });

        var authorizationCode = await authTask;

        Assert.Equal("authorization-code", authorizationCode.Code);
    }

    [Fact]
    public async Task AuthenticateAsync_ThrowsOnMissingCode()
    {
        var runner = new RecordingProcessRunner();
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        await Task.Delay(50);
        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["state"] = authQuery["state"]
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => authTask);

        Assert.Contains("authorization code", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthenticateAsync_ThrowsOnCallbackError()
    {
        var runner = new RecordingProcessRunner();
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        await Task.Delay(50);
        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["state"] = authQuery["state"],
            ["error"] = "access_denied"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => authTask);

        Assert.Equal("Google sign-in failed. Please try again.", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_ReturnsAuthorizationCodeForBackendExchange()
    {
        var runner = new RecordingProcessRunner();
        var service = CreateService("client-id", runner);

        var authTask = service.AuthenticateAsync(CancellationToken.None);
        var authUrl = new Uri(await runner.AuthorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var authQuery = ParseQuery(authUrl.Query);

        Assert.Equal("client-id", authQuery["client_id"]);
        Assert.Equal("code", authQuery["response_type"]);
        Assert.Equal("S256", authQuery["code_challenge_method"]);
        Assert.Equal("select_account", authQuery["prompt"]);

        await Task.Delay(50);
        await SendCallbackAsync(authQuery["redirect_uri"], new Dictionary<string, string>
        {
            ["code"] = "authorization-code",
            ["state"] = authQuery["state"]
        });

        var authorizationCode = await authTask;

        Assert.Equal("client-id", authorizationCode.ClientId);
        Assert.Equal("authorization-code", authorizationCode.Code);
        Assert.Equal(authQuery["redirect_uri"], authorizationCode.RedirectUri);
        Assert.False(string.IsNullOrWhiteSpace(authorizationCode.CodeVerifier));
    }

    private static GoogleOAuthService CreateService(string? clientId, RecordingProcessRunner? runner = null)
    {
        runner ??= new RecordingProcessRunner();
        return new GoogleOAuthService(new GoogleOAuthSettings(clientId), runner);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }

        return result;
    }

    private static async Task SendCallbackAsync(string redirectUri, IReadOnlyDictionary<string, string> query)
    {
        var uri = new Uri(redirectUri, UriKind.Absolute);
        var requestTarget = query.Count == 0
            ? uri.PathAndQuery
            : $"{uri.AbsolutePath}?{string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);

        await using var stream = client.GetStream();
        var request = $"GET {requestTarget} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII);
        _ = await reader.ReadToEndAsync();
    }

    private static async Task SendRawRequestAsync(string redirectUri, string requestTarget)
    {
        var uri = new Uri(redirectUri, UriKind.Absolute);

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);

        await using var stream = client.GetStream();
        var request = $"GET {requestTarget} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.ASCII);
        _ = await reader.ReadToEndAsync();
    }

    private static async Task OpenAndCloseConnectionAsync(string redirectUri)
    {
        var uri = new Uri(redirectUri, UriKind.Absolute);

        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
    }

    private static string CreateTempAppSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public TaskCompletionSource<string> AuthorizationUrl { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProcessResult>? RunAsyncTask { get; init; }
        public bool RunAsyncCalled { get; private set; }
        public bool StartDetachedAsyncCalled { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            RunAsyncCalled = true;
            var argument = arguments.FirstOrDefault() ?? string.Empty;
            AuthorizationUrl.TrySetResult(argument);
            return RunAsyncTask ?? Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }

        public Task<ProcessResult> StartDetachedAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            StartDetachedAsyncCalled = true;
            var argument = arguments.FirstOrDefault() ?? string.Empty;
            AuthorizationUrl.TrySetResult(argument);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
