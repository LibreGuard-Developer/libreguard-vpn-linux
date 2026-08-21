using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;
using Libreguard.Vpn.Linux.ViewModels;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task InitializeAsync_HidesLoginUntilSessionRestoreCompletes()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsInitializing);
        Assert.False(vm.IsUnauthenticated);

        await vm.InitializeAsync();

        Assert.False(vm.IsInitializing);
        Assert.True(vm.IsUnauthenticated);
    }

    [Fact]
    public async Task InitializeAsync_LeavesServerListEmpty_WhenNotAuthenticated()
    {
        var vm = CreateViewModel();

        await vm.InitializeAsync();

        Assert.True(vm.IsUnauthenticated);
        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsQuickConnectCardVisible);
        Assert.Empty(vm.Servers);
        Assert.Empty(vm.VisibleServers);
        Assert.Empty(vm.ServerGroups);
        Assert.Equal(7, vm.UsageChartBars.Count);
        Assert.Equal(7, vm.ConnectionChartBars.Count);
        Assert.Empty(vm.ServerLoadChartBars);
    }

    [Fact]
    public async Task InitializeAsync_ClearsPlaceholderServers_WhenBackendServerLoadFails()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);

        var vm = CreateViewModel(new FakeBackend(tokenValid: true, throwOnGetServers: true), secretStore);

        await vm.InitializeAsync();

        Assert.True(vm.IsAuthenticated);
        Assert.Empty(vm.Servers);
        Assert.Empty(vm.VisibleServers);
        Assert.Contains("Unable to load VPN servers", vm.StatusMessage);
    }

    [Fact]
    public async Task InitializeAsync_RetriesOneTransientServerFailureThenLoadsServers()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var server = new VpnServer(1, "Amsterdam", "10.0.0.1", "nl.example", "Netherlands", "Amsterdam", 100, "free", 10, 1, 443, true);
        var backend = new FakeBackend(
            tokenValid: true,
            transientServerFailures: 1,
            servers: [server]);
        var vm = CreateViewModel(backend, secretStore);

        await vm.InitializeAsync();

        Assert.Equal(2, backend.GetServersCalls);
        Assert.Single(vm.Servers);
        Assert.Equal(server.Id, vm.Servers[0].Id);
        Assert.Equal("Account data refreshed.", vm.StatusMessage);
    }

    [Fact]
    public async Task InitializeAsync_RefreshesAuthenticationInsteadOfTreatingUnauthorizedServerLoadAsTransient()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var backend = new FakeBackend(
            tokenValid: true,
            serverException: new BackendApiException("expired access token", HttpStatusCode.Unauthorized));
        var vm = CreateViewModel(backend, secretStore);

        await vm.InitializeAsync();

        Assert.Equal(2, backend.GetServersCalls);
        Assert.True(vm.IsUnauthenticated);
        Assert.Contains("Please sign in again.", vm.StatusMessage);
    }

    [Fact]
    public void AuthNavigationFlags_TrackSelectedAuthViewAndSection()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsLoginView);

        vm.SelectAuthViewCommand.Execute("Register");
        Assert.True(vm.IsRegisterView);

        vm.SelectAuthViewCommand.Execute("Forgot");
        Assert.True(vm.IsForgotView);

        vm.SelectSectionCommand.Execute("Servers");
        Assert.True(vm.IsServers);
    }

    [Fact]
    public async Task RegistrationUsesResponseEmailOnConfirmationView()
    {
        var backend = new FakeBackend
        {
            RegisterHandler = (_, _) => Task.FromResult(new RegisterResponse(
                true,
                "user-1",
                "registered@example.com",
                true,
                "Check your email."))
        };
        var vm = CreateViewModel(backend);
        vm.Email = "input@example.com";
        vm.Password = "password";
        vm.ConfirmPassword = "password";

        vm.RegisterCommand.Execute(null);
        await WaitForAsync(() => vm.IsEmailConfirmationView);

        Assert.Equal("registered@example.com", vm.Email);
        Assert.Equal("user-1", vm.RegisteredUserId);
    }

    [Fact]
    public async Task SuccessfulLogin_PopulatesAccountEmailFromAuthenticatedSession()
    {
        var backend = new FakeBackend
        {
            LoginHandler = (_, _, device, _) => Task.FromResult(new LoginResponse
            {
                Token = "token",
                RefreshToken = "refresh-token",
                Email = "account@example.com",
                UserId = "user-1",
                DeviceId = device.DeviceId,
                PlanType = "Free"
            })
        };
        var vm = CreateViewModel(backend);
        vm.Email = "login-input@example.com";
        vm.Password = "password";

        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsAuthenticated && vm.AccountEmail == "account@example.com");

        Assert.Equal("account@example.com", vm.AccountEmail);
        Assert.Equal("account@example.com", vm.Email);
    }

    [Fact]
    public async Task ThemeSelection_TracksMutuallyExclusiveState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        Assert.True(vm.IsSystemTheme);

        vm.SelectThemeCommand.Execute("Dark");
        await WaitForAsync(() => vm.IsDarkTheme);

        Assert.True(vm.IsDarkTheme);
        Assert.False(vm.IsLightTheme);
        Assert.False(vm.IsSystemTheme);

        vm.SelectThemeCommand.Execute("Light");
        await WaitForAsync(() => vm.IsLightTheme);

        Assert.True(vm.IsLightTheme);
        Assert.False(vm.IsDarkTheme);
        Assert.False(vm.IsSystemTheme);
    }

    [Fact]
    public async Task ServerSearchSortAndFavorites_UpdatePresentationState()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "A", "10.0.0.1", "a.example", "Germany", "Berlin", 100, "free", 70, 1, 443, true));
        vm.Servers.Add(new VpnServer(2, "B", "10.0.0.2", "b.example", "Germany", "Munich", 100, "pro", 10, 1, 443, true));
        vm.Servers.Add(new VpnServer(3, "C", "10.0.0.3", "c.example", "France", "Paris", 100, "free", 30, 1, 443, true));

        vm.ServerSortMode = "Name";
        Assert.Equal("France", vm.VisibleServers[0].Country);

        vm.ServerSearchText = "France";
        Assert.Single(vm.VisibleServers);
        Assert.Equal("France", vm.VisibleServers[0].Country);
        Assert.Single(vm.ServerGroups);

        vm.SelectServerCommand.Execute(vm.VisibleServers[0]);
        vm.ToggleFavoriteServerCommand.Execute(vm.VisibleServers[0]);

        Assert.Single(vm.RecentServers);
        Assert.Single(vm.FavoriteServers);
        Assert.Equal(vm.VisibleServers[0].Id, vm.RecentServers[0].Id);
        Assert.Equal(vm.VisibleServers[0].Id, vm.FavoriteServers[0].Id);
    }

    [Fact]
    public async Task PingSort_UsesMeasuredLatency()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "Fast", "10.0.0.1", "fast.example", "Germany", "Berlin", 100, "free", 90, 1, 443, true) { PingMs = 40 });
        vm.Servers.Add(new VpnServer(2, "Medium", "10.0.0.2", "medium.example", "France", "Paris", 100, "free", 20, 1, 443, true) { PingMs = 15 });
        vm.Servers.Add(new VpnServer(3, "Slow", "10.0.0.3", "slow.example", "Spain", "Madrid", 100, "free", 10, 1, 443, true) { PingMs = 75 });

        vm.ServerSortMode = "Load";
        vm.ServerSortMode = "Ping";

        Assert.Equal(new[] { 2, 1, 3 }, vm.VisibleServers.Select(server => server.Id).ToArray());
    }

    [Fact]
    public void VpnServer_PingTextReflectsLatencyState()
    {
        var green = new VpnServer(1, "Fast", "10.0.0.1", "fast.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true) { PingMs = 100 };
        var blue = new VpnServer(2, "Medium", "10.0.0.2", "medium.example", "France", "Paris", 100, "free", 20, 1, 443, true) { PingMs = 150 };
        var grey = new VpnServer(3, "Slow", "10.0.0.3", "slow.example", "Spain", "Madrid", 100, "free", 20, 1, 443, true) { PingMs = 250 };

        Assert.Equal("100 ms", green.PingText);
        Assert.Equal("150 ms", blue.PingText);
        Assert.Equal("250 ms", grey.PingText);
        Assert.Equal(Color.Parse("#10B981"), ((ImmutableSolidColorBrush)green.PingBrush).Color);
        Assert.Equal(Color.Parse("#1570EF"), ((ImmutableSolidColorBrush)blue.PingBrush).Color);
        Assert.Equal(Color.Parse("#64748B"), ((ImmutableSolidColorBrush)grey.PingBrush).Color);
    }

    [Fact]
    public async Task InitializeAsync_RefreshesServerLatencyAfterLoadingServers()
    {
        var backend = new FakeBackend(
            tokenValid: true,
            subscriptionIsPro: true,
            servers: new[]
            {
                new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true),
                new VpnServer(2, "Paris", "10.0.0.2", "paris.example", "France", "Paris", 100, "free", 30, 1, 443, true)
            });
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);

        var latencyService = new FakeLatencyService((_, callNumber, _) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["berlin.example"] = 41,
                ["paris.example"] = 88
            }));

        var vm = CreateViewModel(backend: backend, secretStore: secretStore, latencyService: latencyService);

        await vm.InitializeAsync();

        await WaitForAsync(() => latencyService.MeasureCalls == 1);
        await WaitForAsync(() => vm.Servers.All(server => server.PingMs != 0));

        Assert.Equal(41, vm.Servers.First(server => server.ServerHostname == "berlin.example").PingMs);
        Assert.Equal(88, vm.Servers.First(server => server.ServerHostname == "paris.example").PingMs);
        Assert.Equal("41 ms", vm.Servers.First(server => server.ServerHostname == "berlin.example").PingText);
    }

    [Fact]
    public async Task StartLatencyRefresh_DropsStaleResultsWhenServerListChanges()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latencyService = new FakeLatencyService(async (_, callNumber, cancellationToken) =>
        {
            if (callNumber == 1)
            {
                firstStarted.TrySetResult();
                await firstRelease.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["berlin.example"] = 120
                };
            }

            secondStarted.TrySetResult();
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["berlin.example"] = 18
            };
        });
        var vm = CreateViewModel(latencyService: latencyService);

        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "Berlin v1", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true));
        InvokeStartLatencyRefresh(vm);

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(2, "Berlin v2", "10.0.0.2", "berlin.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true));
        InvokeStartLatencyRefresh(vm);

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => vm.Servers[0].PingMs == 18);

        firstRelease.TrySetResult();
        await Task.Delay(250);

        Assert.Equal(18, vm.Servers[0].PingMs);
    }

    [Fact]
    public void SelectServerCommand_ReturnsToDashboardWithSelection()
    {
        var vm = CreateViewModel();
        var server = new VpnServer(99, "Madrid", "203.0.113.10", "madrid.example", "Spain", "Madrid", 100, "free", 42, 17, 443, true);

        vm.CurrentSection = "Servers";
        vm.SelectServerCommand.Execute(server);

        Assert.Equal("Dashboard", vm.CurrentSection);
        Assert.Same(server, vm.SelectedServer);
        Assert.True(vm.IsDashboard);
    }

    [Fact]
    public void DiscardSelectedServerCommand_RestoresQuickConnectState()
    {
        var vm = CreateViewModel();
        var server = new VpnServer(99, "Madrid", "203.0.113.10", "madrid.example", "Spain", "Madrid", 100, "free", 42, 17, 443, true);

        vm.SelectedServer = server;
        vm.DiscardSelectedServerCommand.Execute(null);

        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsQuickConnectCardVisible);
        Assert.False(vm.IsSelectedServerCardVisible);
    }

    [Fact]
    public void ConnectionActionCommand_TracksConnectionState()
    {
        var vm = CreateViewModel();

        Assert.Same(vm.ConnectCommand, vm.ConnectionActionCommand);
        Assert.Equal("Connect", vm.ConnectionActionText);
        Assert.True(vm.IsConnectionIdle);
        Assert.False(vm.IsConnectionConnecting);
        Assert.False(vm.IsConnectionConnected);
        Assert.False(vm.IsConnectionDisconnecting);
        Assert.Equal("Ready to connect", vm.ConnectionStatusText);
        Assert.False(vm.ShouldStrikeOriginalIp);

        vm.ConnectionState = VpnConnectionState.Preparing;
        Assert.True(vm.IsConnectionAttemptActive);
        Assert.True(vm.IsConnectionConnecting);
        Assert.True(vm.ShouldStrikeOriginalIp);
        Assert.Same(vm.CancelConnectionAttemptCommand, vm.ConnectionActionCommand);
        Assert.Equal("Cancel", vm.ConnectionActionText);
        Assert.Equal("Preparing", vm.ConnectionStatusText);

        vm.ConnectionState = VpnConnectionState.Connected;
        Assert.False(vm.IsConnectionAttemptActive);
        Assert.True(vm.IsConnectionConnected);
        Assert.True(vm.ShouldStrikeOriginalIp);

        Assert.Same(vm.DisconnectCommand, vm.ConnectionActionCommand);
        Assert.Equal("Disconnect", vm.ConnectionActionText);
        Assert.Equal("Connected", vm.ConnectionStatusText);

        vm.ConnectionState = VpnConnectionState.Disconnecting;
        Assert.True(vm.IsConnectionDisconnecting);
        Assert.Equal("Disconnecting...", vm.ConnectionActionText);
        Assert.Equal("Disconnecting", vm.ConnectionStatusText);

        vm.ConnectionState = VpnConnectionState.Disconnected;
        Assert.True(vm.IsConnectionIdle);
        Assert.False(vm.ShouldStrikeOriginalIp);
        Assert.Equal("Ready to connect", vm.ConnectionStatusText);
    }

    [Theory]
    [InlineData(VpnConnectionState.Disconnected, true, false, false, false, "Ready to connect")]
    [InlineData(VpnConnectionState.Error, true, false, false, false, "Ready to connect")]
    [InlineData(VpnConnectionState.Preparing, false, true, false, false, "Preparing")]
    [InlineData(VpnConnectionState.Connecting, false, true, false, false, "Connecting")]
    [InlineData(VpnConnectionState.Connected, false, false, true, false, "Connected")]
    [InlineData(VpnConnectionState.Disconnecting, false, false, false, true, "Disconnecting")]
    public void ConnectionVisualStateFlags_TrackConnectionLifecycle(
        VpnConnectionState state,
        bool isIdle,
        bool isConnecting,
        bool isConnected,
        bool isDisconnecting,
        string expectedStatusText)
    {
        var vm = CreateViewModel();

        vm.ConnectionState = state;

        Assert.Equal(isIdle, vm.IsConnectionIdle);
        Assert.Equal(isConnecting, vm.IsConnectionConnecting);
        Assert.Equal(isConnected, vm.IsConnectionConnected);
        Assert.Equal(isDisconnecting, vm.IsConnectionDisconnecting);
        Assert.Equal(expectedStatusText, vm.ConnectionStatusText);
    }

    [Fact]
    public async Task ConnectedVpnStatus_PopulatesDashboardTunnelMetrics()
    {
        var vpn = new FakeVpnConnectionService();
        var trafficMonitor = new FakeTunnelTrafficMonitor
        {
            StartSnapshot = new TunnelTrafficSnapshot("lgvpn0", 1024, 2048, 3 * 1024, 5 * 1024, true)
        };
        var vm = CreateViewModel(vpn: vpn, tunnelTrafficMonitor: trafficMonitor);
        var server = new VpnServer(99, "Berlin Server", "203.0.113.40", "berlin.example", "Germany", "Berlin", 100, "free", 42, 17, 443, true);

        vm.SelectedServer = server;
        vm.ConnectCommand.Execute(null);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        vpn.RaiseStatus(new VpnStatus(
            VpnConnectionState.Preparing,
            "profile",
            "Preparing",
            null,
            "198.51.100.15",
            "203.0.113.40"));

        await WaitForAsync(() => vm.OriginalPublicIpText == "198.51.100.15");
        Assert.True(vm.ShouldStrikeOriginalIp);
        Assert.Equal("203.0.113.40", vm.VpnIpText);

        vpn.RaiseStatus(new VpnStatus(
            VpnConnectionState.Connected,
            "profile",
            "Connected",
            DateTimeOffset.UtcNow.AddSeconds(-5),
            "198.51.100.15",
            "203.0.113.40"));

        await WaitForAsync(() => vm.VpnIpText == "203.0.113.40");

        Assert.Equal("198.51.100.15", vm.OriginalPublicIpText);
        Assert.Equal("203.0.113.40", vm.VpnIpText);
        Assert.Equal("1 KB/s", vm.LiveDownloadSpeedText);
        Assert.Equal("2 KB/s", vm.LiveUploadSpeedText);
        Assert.Equal("8 KB", vm.SessionDataTotalText);
        Assert.Equal("Berlin, Germany", vm.FormattedConnectedLocationText);
        Assert.Equal("profile", trafficMonitor.LastStartedProfile);
        Assert.NotEqual("00:00:00", vm.ConnectionDurationText);
    }

    [Fact]
    public async Task DisconnectOrError_ClearsDashboardTunnelMetrics()
    {
        var vpn = new FakeVpnConnectionService();
        var trafficMonitor = new FakeTunnelTrafficMonitor
        {
            StartSnapshot = new TunnelTrafficSnapshot("lgvpn0", 1024, 2048, 3 * 1024, 5 * 1024, true)
        };
        var vm = CreateViewModel(vpn: vpn, tunnelTrafficMonitor: trafficMonitor);
        var server = new VpnServer(99, "Berlin Server", "203.0.113.40", "berlin.example", "Germany", "Berlin", 100, "free", 42, 17, 443, true);

        vm.SelectedServer = server;
        vm.ConnectCommand.Execute(null);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        vpn.RaiseStatus(new VpnStatus(
            VpnConnectionState.Connected,
            "profile",
            "Connected",
            DateTimeOffset.UtcNow.AddSeconds(-2),
            "198.51.100.15",
            "203.0.113.40"));
        await WaitForAsync(() => vm.SessionDataTotalText == "8 KB");

        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Disconnected, null, "Disconnected"));
        await WaitForAsync(() => vm.SessionDataTotalText == "0 B");

        Assert.Equal("00:00:00", vm.ConnectionDurationText);
        Assert.Equal("198.51.100.15", vm.OriginalPublicIpText);
        Assert.Equal("—", vm.VpnIpText);
        Assert.Equal("0 B/s", vm.LiveDownloadSpeedText);
        Assert.Equal("0 B/s", vm.LiveUploadSpeedText);
        Assert.False(vm.ShouldStrikeOriginalIp);
        Assert.True(trafficMonitor.StopCalled);
    }

    [Fact]
    public async Task PrepareForExitAsync_ShutsDownActiveVpnState()
    {
        var vpn = new FakeVpnConnectionService();
        var vm = CreateViewModel(vpn: vpn);
        vm.ConnectionState = VpnConnectionState.Connected;

        var success = await vm.PrepareForExitAsync(CancellationToken.None);

        Assert.True(success);
        Assert.Equal(1, vpn.ShutdownCalls);
        Assert.Equal("Disconnecting before exit...", vm.StatusMessage);
    }

    [Fact]
    public async Task PrepareForExitAsync_IsNoOpWhileDisconnected()
    {
        var vpn = new FakeVpnConnectionService();
        var vm = CreateViewModel(vpn: vpn);

        var success = await vm.PrepareForExitAsync(CancellationToken.None);

        Assert.True(success);
        Assert.Equal(0, vpn.ShutdownCalls);
    }

    [Fact]
    public async Task QuickConnectCommand_UsesBestServerSelection()
    {
        var latencyService = new FakeLatencyService(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast.example"] = 18,
            ["slow.example"] = 180,
            ["premium.example"] = 22
        });
        var backend = new FakeBackend(tokenValid: true);
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);
        var vpn = new FakeVpnConnectionService();
        var vm = CreateViewModel(backend: backend, secretStore: secretStore, vpn: vpn, latencyService: latencyService);
        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "Fast", "10.0.0.1", "fast.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true));
        vm.Servers.Add(new VpnServer(2, "Slow", "10.0.0.2", "slow.example", "France", "Paris", 100, "free", 80, 1, 443, true));
        vm.Servers.Add(new VpnServer(3, "Premium", "10.0.0.3", "premium.example", "United States", "New York", 100, "pro", 10, 1, 443, true));

        vm.QuickConnectCommand.Execute(null);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        Assert.Equal(1, latencyService.MeasureCalls);
        Assert.Equal(1, vpn.LastConnectedServer?.Id);
        Assert.Equal(1, vm.ConnectedServer?.Id);
    }

    [Fact]
    public async Task QuickConnectCommand_FallsBackToFirstEligibleServerWhenLatencyProbeFails()
    {
        var backend = new FakeBackend(tokenValid: true);
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);
        var vpn = new FakeVpnConnectionService();
        var latencyService = new FakeLatencyService((_, _, _) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["premium.example"] = -1,
            ["free.example"] = -1
        }));
        var vm = CreateViewModel(backend: backend, secretStore: secretStore, vpn: vpn, latencyService: latencyService);
        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "Premium", "10.0.0.1", "premium.example", "United States", "New York", 100, "pro", 10, 1, 443, true));
        vm.Servers.Add(new VpnServer(2, "Free", "10.0.0.2", "free.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true));

        vm.QuickConnectCommand.Execute(null);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        Assert.Equal(1, latencyService.MeasureCalls);
        Assert.Equal(2, vpn.LastConnectedServer?.Id);
        Assert.Equal(2, vm.ConnectedServer?.Id);
    }

    [Fact]
    public async Task QuickConnectCommand_ShowsCancelDuringAttempt_AndCanBeCancelled()
    {
        var backend = new FakeBackend(tokenValid: true);
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);
        var vpn = new FakeVpnConnectionService { HoldConnectOpen = true };
        var vm = CreateViewModel(backend: backend, secretStore: secretStore, vpn: vpn);
        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "Fast", "10.0.0.1", "fast.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true));

        vm.QuickConnectCommand.Execute(null);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        Assert.True(vm.IsQuickConnectRunning);
        Assert.True(vm.IsConnectionAttemptActive);
        Assert.Same(vm.CancelConnectionAttemptCommand, vm.ConnectionActionCommand);
        Assert.Equal("Cancel", vm.ConnectionActionText);

        vm.ConnectionActionCommand.Execute(null);
        await WaitForAsync(() => !vm.IsQuickConnectRunning);

        Assert.False(vm.IsConnectionAttemptActive);
        Assert.Equal("Connect", vm.ConnectionActionText);
        Assert.Same(vm.ConnectCommand, vm.ConnectionActionCommand);
    }

    [Fact]
    public void ServerSelectionHelper_FreeUsersExcludePremiumServers()
    {
        var servers = new[]
        {
            new VpnServer(1, "Free", "10.0.0.1", "free.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true),
            new VpnServer(2, "Premium", "10.0.0.2", "premium.example", "United States", "New York", 100, "pro", 5, 1, 443, true)
        };

        var best = ServerSelectionHelper.SelectBestServer(servers, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["free.example"] = 40,
            ["premium.example"] = 5
        }, isPro: false);

        Assert.Equal(1, best?.Id);
    }

    [Fact]
    public void ServerSelectionHelper_ProUsersFavorPremiumServers()
    {
        var servers = new[]
        {
            new VpnServer(1, "Free", "10.0.0.1", "free.example", "Germany", "Berlin", 100, "free", 20, 1, 443, true),
            new VpnServer(2, "Premium", "10.0.0.2", "premium.example", "United States", "New York", 100, "pro", 20, 1, 443, true)
        };

        var best = ServerSelectionHelper.SelectBestServer(servers, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["free.example"] = 40,
            ["premium.example"] = 40
        }, isPro: true);

        Assert.Equal(2, best?.Id);
    }

    [Fact]
    public void ServerSelectionHelper_HighLoadUsesBalancedWeighting()
    {
        var servers = new[]
        {
            new VpnServer(1, "FastButLoaded", "10.0.0.1", "loaded.example", "Germany", "Berlin", 100, "free", 90, 1, 443, true),
            new VpnServer(2, "SlowerButIdle", "10.0.0.2", "idle.example", "France", "Paris", 100, "free", 10, 1, 443, true)
        };

        var best = ServerSelectionHelper.SelectBestServer(servers, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["loaded.example"] = 10,
            ["idle.example"] = 200
        }, isPro: false);

        Assert.Equal(2, best?.Id);
    }

    [Fact]
    public void ServerSelectionHelper_SelectsDeterministicHighestScore()
    {
        var servers = new[]
        {
            new VpnServer(1, "Berlin", "10.0.0.1", "berlin.example", "Germany", "Berlin", 100, "free", 25, 1, 443, true),
            new VpnServer(2, "Paris", "10.0.0.2", "paris.example", "France", "Paris", 100, "free", 55, 1, 443, true),
            new VpnServer(3, "Madrid", "10.0.0.3", "madrid.example", "Spain", "Madrid", 100, "free", 15, 1, 443, true)
        };

        var best = ServerSelectionHelper.SelectBestServer(servers, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["berlin.example"] = 25,
            ["paris.example"] = 70,
            ["madrid.example"] = 110
        }, isPro: false);

        Assert.Equal(1, best?.Id);
    }

    [Theory]
    [InlineData("Spain", "ES")]
    [InlineData("Switzerland", "CH")]
    [InlineData("Finland", "FI")]
    public void CountryFlag_ResolvesCommonEuropeanCountries(string country, string expectedIsoCode)
    {
        var server = new VpnServer(1, "Test", "127.0.0.1", null, country, null, 100, null, null, null, null, true);

        var expectedFlag = string.Concat(expectedIsoCode.Select(ch => char.ConvertFromUtf32(0x1F1E6 + (char.ToUpperInvariant(ch) - 'A'))));

        Assert.Equal(expectedFlag, server.CountryFlag);
    }

    [Theory]
    [InlineData("DE-MULTI-1", "DE")]
    [InlineData("us-multi-2", "US")]
    [InlineData("FL-MULTI-1", "FI")]
    public void CertificateCountryFlag_ResolvesServerNamePrefix(string serverName, string expectedIsoCode)
    {
        var certificate = new UserCertificate { ServerName = serverName };

        var expectedFlag = string.Concat(expectedIsoCode.Select(ch => char.ConvertFromUtf32(0x1F1E6 + (char.ToUpperInvariant(ch) - 'A'))));

        Assert.Equal(expectedFlag, certificate.CountryFlag);
    }

    [Fact]
    public async Task StatisticsPeriod_ChangesChartShape()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        var weekCount = vm.UsageChartBars.Count;
        Assert.Equal("0 B", vm.StatisticsTotalDataText);
        Assert.Equal("0", vm.StatisticsConnectionsText);
        Assert.Equal("0m", vm.StatisticsAverageSessionText);
        vm.SelectedStatisticsPeriod = "Month";

        Assert.NotEqual(weekCount, vm.UsageChartBars.Count);
        Assert.Equal(6, vm.UsageChartBars.Count);
        Assert.Equal(6, vm.DailyTrafficRows.Count);
        Assert.Equal(6, vm.ConnectionDurationRows.Count);
        Assert.True(vm.IsMonthStatisticsPeriod);
    }

    [Fact]
    public async Task StatisticsPresentation_UsesRequestedMetrics()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);
        var vpn = new FakeVpnConnectionService();
        var trafficMonitor = new FakeTunnelTrafficMonitor
        {
            StartSnapshot = new TunnelTrafficSnapshot("lgvpn0", 0, 0, 3 * 1024, 5 * 1024, true)
        };
        var vm = CreateViewModel(new FakeBackend(tokenValid: true), secretStore, vpn, tunnelTrafficMonitor: trafficMonitor);
        await vm.InitializeAsync();

        vm.Servers.Clear();
        vm.Servers.Add(new VpnServer(1, "Amsterdam", "10.0.0.1", "ams.example", "Netherlands", "Amsterdam", 100, "free", 20, 4, 443, true));
        vm.SelectedServer = vm.Servers[0];
        vm.SelectedStatisticsPeriod = "Month";
        vm.ConnectCommand.Execute(null);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        vpn.RaiseStatus(new VpnStatus(
            VpnConnectionState.Connected,
            "profile",
            "Connected",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            "198.51.100.15",
            "10.0.0.1"));

        await WaitForAsync(() => vm.StatisticsTotalDataText == "8 KB");

        Assert.Equal("1", vm.StatisticsConnectionsText);
        Assert.NotEqual("0m", vm.StatisticsAverageSessionText);
        Assert.Equal("3 KB", vm.StatisticsAverageDownloadText);
        Assert.Equal("3 KB", vm.StatisticsTotalDownloadText);
        Assert.Equal("5 KB", vm.StatisticsTotalUploadText);
        Assert.Equal(6, vm.DailyTrafficRows.Count);
        Assert.Contains(vm.DailyTrafficRows, row => row.TotalText == "8 KB");
        Assert.Equal(6, vm.ConnectionDurationRows.Count);
        Assert.Contains(vm.ServerLoadChartBars, bar => bar.Label == "Amsterdam");
        Assert.Equal(6, vm.UsageChartBars.Count);

        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Disconnected, null, "Disconnected"));
        await WaitForAsync(() => vm.SessionDataTotalText == "0 B");

        Assert.Equal("8 KB", vm.StatisticsTotalDataText);
        Assert.Equal("1", vm.StatisticsConnectionsText);
    }

    [Fact]
    public async Task OpenVpnProtocol_IsLockedOnFreePlan()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync();

        vm.SelectProtocolCommand.Execute("OpenVPN");

        Assert.Equal("IKEv2", vm.SelectedProtocol);
        Assert.True(vm.IsIkev2Protocol);
        Assert.False(vm.IsOpenVpnProtocol);
    }

    [Fact]
    public void SelectingActiveProtocol_ReassertsRadioSelection()
    {
        var vm = CreateViewModel();

        vm.SelectProtocolCommand.Execute("IKEv2");

        Assert.Equal("IKEv2", vm.SelectedProtocol);
        Assert.True(vm.IsIkev2Protocol);
        Assert.False(vm.IsOpenVpnProtocol);
    }

    [Fact]
    public void MonthlyUsage_FormatsFreeAndUnlimitedPlans()
    {
        var vm = CreateViewModel();

        vm.Subscription = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null);
        vm.Quota = new UsageQuota { BytesUsed = 2L * 1024 * 1024 * 1024, BytesLimit = null, IsUnlimited = false };

        Assert.Equal("2 GB / 5 GB", vm.MonthlyUsageDisplayText);
        Assert.False(vm.IsMonthlyUsageUnlimited);
        Assert.True(vm.ShowMonthlyUsageProgress);

        vm.Subscription = new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 1, 3, true, null);
        vm.Quota = new UsageQuota { BytesUsed = 512L * 1024 * 1024, BytesLimit = null, IsUnlimited = true };

        Assert.Equal("512 MB / ∞", vm.MonthlyUsageDisplayText);
        Assert.True(vm.IsMonthlyUsageUnlimited);
        Assert.False(vm.ShowMonthlyUsageProgress);
    }

    [Fact]
    public void TrayToolTipText_TracksConnectionStateAndFreeUsage()
    {
        var vm = CreateViewModel();
        var server = new VpnServer(99, "Berlin Server", "203.0.113.40", "berlin.example", "Germany", "Berlin", 100, "free", 42, 17, 443, true);

        vm.Subscription = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null);
        vm.Quota = new UsageQuota { BytesUsed = 2L * 1024 * 1024 * 1024, BytesLimit = null, IsUnlimited = false };

        Assert.Equal("LibreGuard VPN - Not Connected - Monthly usage: 2 GB / 5 GB", vm.TrayToolTipText);

        vm.ConnectionState = VpnConnectionState.Connecting;
        Assert.Equal("LibreGuard VPN - Connecting - Monthly usage: 2 GB / 5 GB", vm.TrayToolTipText);

        vm.SelectedServer = server;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ConnectedServer))!
            .SetValue(vm, server);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.VpnIpText))!
            .SetValue(vm, "203.0.113.40");
        vm.ConnectionState = VpnConnectionState.Connected;

        Assert.Equal("LibreGuard VPN - Germany, Berlin - 203.0.113.40 - Session data: 0 B - Monthly usage: 2 GB / 5 GB", vm.TrayToolTipText);
    }

    [Fact]
    public void TrayMenuState_ChangesWhenConnected()
    {
        var vm = CreateViewModel();

        Assert.Equal("Quick Connect", vm.TrayTopActionText);
        Assert.True(vm.CanUseTrayServers);

        vm.ConnectionState = VpnConnectionState.Connected;

        Assert.Equal("Disconnect", vm.TrayTopActionText);
        Assert.False(vm.CanUseTrayServers);
    }

    [Fact]
    public void FreePlan_CannotUsePremiumServerFromTray()
    {
        var vm = CreateViewModel();
        var freeServer = new VpnServer(100, "Free Server", "203.0.113.41", "free.example", "Germany", "Berlin", 100, "free", 20, 10, 443, true);
        var proServer = new VpnServer(101, "Pro Server", "203.0.113.42", "pro.example", "Germany", "Frankfurt", 100, "pro", 20, 10, 443, true);

        vm.Subscription = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 1, true, null);

        Assert.True(vm.CanUseTrayServer(freeServer));
        Assert.False(vm.CanUseTrayServer(proServer));
    }

    [Fact]
    public void SelectingPremiumServerAsFreeUser_NavigatesToUpgrade()
    {
        var vm = CreateViewModel();
        var proServer = new VpnServer(101, "Pro Server", "203.0.113.42", "pro.example", "Germany", "Frankfurt", 100, "pro", 20, 10, 443, true);
        vm.Subscription = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 1, true, null);

        vm.SelectServerCommand.Execute(proServer);

        Assert.Equal("Upgrade", vm.CurrentSection);
        Assert.Contains("Upgrade to Pro", vm.StatusMessage);
        Assert.Null(vm.SelectedServer);
    }

    [Fact]
    public async Task ConnectToServerCommand_SelectsServerAndStartsConnection()
    {
        var backend = new FakeBackend(tokenValid: true);
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);
        var vpn = new FakeVpnConnectionService();
        var vm = CreateViewModel(backend: backend, secretStore: secretStore, vpn: vpn);
        var server = new VpnServer(99, "Berlin Server", "203.0.113.40", "berlin.example", "Germany", "Berlin", 100, "free", 42, 17, 443, true);

        vm.ConnectToServerCommand.Execute(server);
        await WaitForAsync(() => vpn.LastConnectedServer is not null);

        Assert.Same(server, vm.SelectedServer);
        Assert.Equal("Dashboard", vm.CurrentSection);
        Assert.Equal(99, vpn.LastConnectedServer?.Id);
    }

    [Fact]
    public async Task Notifications_AreSentForConnectionTransitions_WhenEnabled()
    {
        var vpn = new FakeVpnConnectionService();
        var notifications = new FakeDesktopNotificationService();
        var vm = CreateViewModel(vpn: vpn, desktopNotifications: notifications);
        var server = new VpnServer(99, "Berlin Server", "203.0.113.40", "berlin.example", "Germany", "Berlin", 100, "free", 42, 17, 443, true);

        vm.SelectedServer = server;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.ConnectedServer))!
            .SetValue(vm, server);

        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Preparing, null, "Preparing"));
        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Connecting, null, "Connecting"));
        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Connected, "profile", "Connected"));
        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Disconnected, null, "Disconnected"));
        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Error, null, "Failed"));

        await WaitForAsync(() => notifications.Messages.Count == 4);

        Assert.Equal(
            ["LibreGuard VPN - Connecting", "LibreGuard VPN - Connected", "LibreGuard VPN - Disconnected", "LibreGuard VPN - Connection error"],
            notifications.Messages.Select(message => message.Title).ToArray());
    }

    [Fact]
    public async Task Notifications_AreSkipped_WhenDisabled()
    {
        var vpn = new FakeVpnConnectionService();
        var notifications = new FakeDesktopNotificationService();
        var vm = CreateViewModel(vpn: vpn, desktopNotifications: notifications);

        vm.NotificationsEnabled = false;
        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Connected, "profile", "Connected"));
        await Task.Delay(20);

        Assert.Empty(notifications.Messages);
    }

    [Fact]
    public void PlanState_TrustsSubscriptionIsProOverStalePlanLabel()
    {
        var vm = CreateViewModel();

        vm.Subscription = new SubscriptionStatus("Pro", false, "Active", null, "Monthly", 1, 3, true, null);

        Assert.Equal("Free", vm.PlanText);
        Assert.False(vm.IsProPlan);
        Assert.True(vm.IsFreePlan);
        Assert.True(vm.ShowUpgradeSettingsCard);
        Assert.False(vm.CanAccessCertificates);
    }

    [Fact]
    public async Task InitializeAsync_LoadsProAdBlockingPreferenceAndEffectiveStatus()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(true, true, true, "AdBlocking", 21)
        };

        var vm = await CreateAuthenticatedViewModelAsync(backend);

        Assert.Equal(1, backend.GetDnsPreferenceCalls);
        Assert.True(vm.IsAdBlockingSettingsAvailable);
        Assert.True(vm.AdBlockingEnabled);
        Assert.True(vm.IsAdBlockingEffectivelyEnabled);
        Assert.True(vm.CanToggleAdBlocking);
        Assert.False(vm.ShowAdBlockingProBadge);
        Assert.Contains("Active", vm.AdBlockingStatusText);
        Assert.Contains("AdBlocking", vm.AdBlockingStatusText);
        Assert.Equal("Changes can take up to 21 seconds to reach VPN servers.", vm.AdBlockingPropagationText);
    }

    [Fact]
    public async Task FreeAdBlocking_RemainsRequestedButLockedAndPaused()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(true, false, false, "Standard", 15)
        };

        var vm = await CreateAuthenticatedViewModelAsync(backend);

        Assert.True(vm.AdBlockingEnabled);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.True(vm.ShowAdBlockingProBadge);
        Assert.True(vm.ShowAdBlockingUpgradeAction);
        Assert.Equal("Paused—Pro required.", vm.AdBlockingStatusText);

        vm.AdBlockingEnabled = false;

        Assert.True(vm.AdBlockingEnabled);
        Assert.Equal(0, backend.UpdateDnsPreferenceCalls);
    }

    [Fact]
    public async Task FreeAdBlockingOff_ExplainsProRequirement()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(false, false, false, "Standard", 15)
        };

        var vm = await CreateAuthenticatedViewModelAsync(backend);

        Assert.Equal("Available with Pro—upgrade to enable ad blocking.", vm.AdBlockingStatusText);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.True(vm.ShowAdBlockingUpgradeAction);
        Assert.False(vm.ShowAdBlockingPropagation);
        Assert.Empty(vm.AdBlockingPropagationText);
    }

    [Fact]
    public async Task AdBlockingGate_IgnoresStalePersistedProPlanLabel()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            SubscriptionStatusResponse = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null),
            DnsPreferenceResponse = new DnsPreferenceResponse(false, true, false, "Standard", 15)
        };
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        await secretStore.SetAsync("plan-type", "Pro", CancellationToken.None);
        var vm = CreateViewModel(backend, secretStore);

        await vm.InitializeAsync();

        Assert.Equal("Free", vm.PlanText);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.True(vm.ShowAdBlockingProBadge);

        vm.AdBlockingEnabled = true;

        Assert.False(vm.AdBlockingEnabled);
        Assert.Equal(0, backend.UpdateDnsPreferenceCalls);
    }

    [Fact]
    public async Task DnsPreferenceFailure_DisablesOnlyAdBlockingCard()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceHandler = _ => Task.FromException<DnsPreferenceResponse>(new HttpRequestException("DNS settings unavailable"))
        };

        var vm = await CreateAuthenticatedViewModelAsync(backend);

        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.Subscription?.IsPro);
        Assert.NotNull(vm.Quota);
        Assert.False(vm.IsAdBlockingSettingsAvailable);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.Contains("Status unavailable", vm.AdBlockingStatusText);
        Assert.Equal("Account data refreshed.", vm.StatusMessage);
    }

    [Fact]
    public async Task EnteringSettings_RefreshesDnsPreference()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true);
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        backend.ResetAccountStateCallCounts();

        vm.SelectSectionCommand.Execute("Settings");
        await WaitForAsync(() => backend.GetDnsPreferenceCalls == 1);

        Assert.Equal(1, backend.GetSubscriptionStatusCalls);
        Assert.Equal(1, backend.GetDnsPreferenceCalls);
    }

    [Fact]
    public async Task AccountRefresh_DoesNotOverwriteNewerAdBlockingToggleResult()
    {
        var refreshDnsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshDnsReleased = new TaskCompletionSource<DnsPreferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dnsLoadNumber = 0;
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(false, true, false, "Standard", 15),
            DnsPreferenceHandler = async cancellationToken =>
            {
                if (Interlocked.Increment(ref dnsLoadNumber) == 1)
                {
                    return new DnsPreferenceResponse(false, true, false, "Standard", 15);
                }

                refreshDnsStarted.TrySetResult();
                return await refreshDnsReleased.Task.WaitAsync(cancellationToken);
            },
            UpdateDnsPreferenceHandler = (enabled, _) => Task.FromResult(
                new DnsPreferenceResponse(enabled, true, enabled, enabled ? "AdBlocking" : "Standard", 15))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.SelectSectionCommand.Execute("Settings");
        await refreshDnsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => backend.UpdateDnsPreferenceCalls == 1 && !vm.IsUpdatingAdBlocking);
        Assert.True(vm.AdBlockingEnabled);
        Assert.True(vm.IsAdBlockingEffectivelyEnabled);

        refreshDnsReleased.TrySetResult(new DnsPreferenceResponse(false, true, false, "Standard", 15));
        await WaitForAsync(() => vm.StatusMessage == "Account data refreshed.");

        Assert.True(vm.AdBlockingEnabled);
        Assert.True(vm.IsAdBlockingEffectivelyEnabled);
    }

    [Fact]
    public async Task AdBlockingToggle_UpdatesWhileConnectedWithoutReconnectAndDisablesWhilePending()
    {
        var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateReleased = new TaskCompletionSource<DnsPreferenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(false, true, false, "Standard", 15),
            UpdateDnsPreferenceHandler = async (enabled, cancellationToken) =>
            {
                updateStarted.TrySetResult();
                return await updateReleased.Task.WaitAsync(cancellationToken);
            }
        };
        var vpn = new FakeVpnConnectionService();
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        var vm = CreateViewModel(backend, secretStore, vpn);
        await vm.InitializeAsync();
        vpn.RaiseStatus(new VpnStatus(VpnConnectionState.Connected, "profile", "Connected"));

        vm.AdBlockingEnabled = true;
        await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(vm.AdBlockingEnabled);
        Assert.True(vm.IsUpdatingAdBlocking);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.True(vm.IsConnected);
        Assert.Equal(0, vpn.ConnectCalls);
        Assert.Equal(0, vpn.DisconnectCalls);

        updateReleased.TrySetResult(new DnsPreferenceResponse(true, true, true, "AdBlocking", 15));
        await WaitForAsync(() => !vm.IsUpdatingAdBlocking);

        Assert.True(vm.IsAdBlockingEffectivelyEnabled);
        Assert.True(vm.CanToggleAdBlocking);
        Assert.Equal(true, backend.LastRequestedAdBlockingEnabled);
        Assert.Equal(0, vpn.ConnectCalls);
        Assert.Equal(0, vpn.DisconnectCalls);
    }

    [Fact]
    public async Task AdBlockingToggleFailure_RestoresPreviousAuthoritativeState()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(false, true, false, "Standard", 15),
            UpdateDnsPreferenceHandler = (_, _) => Task.FromException<DnsPreferenceResponse>(new HttpRequestException("update failed"))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => backend.UpdateDnsPreferenceCalls == 1 && !vm.IsUpdatingAdBlocking);

        Assert.False(vm.AdBlockingEnabled);
        Assert.False(vm.IsAdBlockingEffectivelyEnabled);
        Assert.Equal("Ad Blocking could not be updated. Your previous setting was restored.", vm.StatusMessage);
    }

    [Fact]
    public async Task AdBlockingToggle_UnauthorizedResponseUsesAuthorizedRetry()
    {
        var updateAttempts = 0;
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(false, true, false, "Standard", 15),
            UpdateDnsPreferenceHandler = (enabled, _) => Interlocked.Increment(ref updateAttempts) == 1
                ? Task.FromException<DnsPreferenceResponse>(new BackendApiException("expired", HttpStatusCode.Unauthorized))
                : Task.FromResult(new DnsPreferenceResponse(enabled, true, enabled, "AdBlocking", 15))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => backend.UpdateDnsPreferenceCalls == 2 && !vm.IsUpdatingAdBlocking);

        Assert.True(vm.AdBlockingEnabled);
        Assert.True(vm.IsAdBlockingEffectivelyEnabled);
        Assert.Equal(2, backend.UpdateDnsPreferenceCalls);
    }

    [Fact]
    public async Task AdBlockingProRequired_RefetchesAuthoritativeStateAndNavigatesToUpgrade()
    {
        var subscriptions = new Queue<SubscriptionStatus>([
            new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 1, 3, true, null),
            new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null)
        ]);
        var dnsPreferences = new Queue<DnsPreferenceResponse>([
            new DnsPreferenceResponse(false, true, false, "Standard", 15),
            new DnsPreferenceResponse(true, false, false, "Standard", 15)
        ]);
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            SubscriptionStatusHandler = _ => Task.FromResult(subscriptions.Dequeue()),
            DnsPreferenceHandler = _ => Task.FromResult(dnsPreferences.Dequeue()),
            UpdateDnsPreferenceHandler = (_, _) => Task.FromException<DnsPreferenceResponse>(
                new BackendApiException("Pro required", HttpStatusCode.Forbidden, "{\"errorCode\":\"PRO_REQUIRED\"}"))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => vm.CurrentSection == "Upgrade" && !vm.IsUpdatingAdBlocking);

        Assert.Equal(2, backend.GetDnsPreferenceCalls);
        Assert.Equal(2, backend.GetSubscriptionStatusCalls);
        Assert.False(vm.Subscription?.IsPro);
        Assert.True(vm.AdBlockingEnabled);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.Equal("Paused—Pro required.", vm.AdBlockingStatusText);
        Assert.Equal("Ad Blocking is available with a Pro subscription.", vm.StatusMessage);
    }

    [Fact]
    public async Task AdBlockingProRequired_DoesNotLetStaleAccountRefreshRestoreProSubscription()
    {
        var staleDnsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleDns = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleDnsReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptionLoadNumber = 0;
        var dnsLoadNumber = 0;
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            SubscriptionStatusHandler = _ => Interlocked.Increment(ref subscriptionLoadNumber) switch
            {
                1 or 2 => Task.FromResult(new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 1, 3, true, null)),
                _ => Task.FromResult(new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null))
            },
            DnsPreferenceHandler = async _ =>
            {
                var loadNumber = Interlocked.Increment(ref dnsLoadNumber);
                if (loadNumber == 2)
                {
                    staleDnsStarted.TrySetResult();
                    await releaseStaleDns.Task;
                    staleDnsReturned.TrySetResult();
                    return new DnsPreferenceResponse(false, true, false, "Standard", 15);
                }

                return loadNumber == 1
                    ? new DnsPreferenceResponse(false, true, false, "Standard", 15)
                    : new DnsPreferenceResponse(true, false, false, "Standard", 15);
            },
            UpdateDnsPreferenceHandler = (_, _) => Task.FromException<DnsPreferenceResponse>(
                new BackendApiException("Pro required", HttpStatusCode.Forbidden, "{\"errorCode\":\"PRO_REQUIRED\"}"))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.SelectSectionCommand.Execute("Settings");
        await staleDnsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => vm.CurrentSection == "Upgrade" && !vm.IsUpdatingAdBlocking);

        Assert.False(vm.Subscription?.IsPro);
        Assert.Equal("Free", vm.PlanText);

        releaseStaleDns.TrySetResult();
        await staleDnsReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.False(vm.Subscription?.IsPro);
        Assert.Equal("Free", vm.PlanText);
        Assert.False(vm.CanAccessCertificates);
        Assert.False(vm.CanToggleAdBlocking);
    }

    [Fact]
    public async Task AdBlockingProRequired_WhenRecoveryFails_LeavesCardUnavailableAndLocked()
    {
        var dnsLoadNumber = 0;
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceHandler = _ => Interlocked.Increment(ref dnsLoadNumber) == 1
                ? Task.FromResult(new DnsPreferenceResponse(false, true, false, "Standard", 15))
                : Task.FromException<DnsPreferenceResponse>(new HttpRequestException("recovery failed")),
            UpdateDnsPreferenceHandler = (_, _) => Task.FromException<DnsPreferenceResponse>(
                new BackendApiException("Pro required", HttpStatusCode.Forbidden, "{\"errorCode\":\"PRO_REQUIRED\"}"))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => vm.CurrentSection == "Upgrade" && !vm.IsUpdatingAdBlocking);

        Assert.False(vm.IsAdBlockingSettingsAvailable);
        Assert.False(vm.AdBlockingEnabled);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.Contains("Status unavailable", vm.AdBlockingStatusText);
        Assert.Equal("Ad Blocking is available with a Pro subscription.", vm.StatusMessage);
    }

    [Fact]
    public async Task AdBlockingProRequired_RemainsLockedWhenSubscriptionRecoveryFails()
    {
        var subscriptionLoadNumber = 0;
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceHandler = _ => Task.FromResult(
                new DnsPreferenceResponse(false, true, false, "Standard", 15)),
            SubscriptionStatusHandler = _ => Interlocked.Increment(ref subscriptionLoadNumber) == 1
                ? Task.FromResult(new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 1, 3, true, null))
                : Task.FromException<SubscriptionStatus>(new HttpRequestException("subscription recovery failed")),
            UpdateDnsPreferenceHandler = (_, _) => Task.FromException<DnsPreferenceResponse>(
                new BackendApiException("Pro required", HttpStatusCode.Forbidden, "{\"errorCode\":\"PRO_REQUIRED\"}"))
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.AdBlockingEnabled = true;
        await WaitForAsync(() => vm.CurrentSection == "Upgrade" && !vm.IsUpdatingAdBlocking);

        Assert.True(vm.IsAdBlockingSettingsAvailable);
        Assert.True(vm.Subscription?.IsPro);
        Assert.False(vm.CanToggleAdBlocking);
        Assert.True(vm.ShowAdBlockingProBadge);
        Assert.True(vm.ShowAdBlockingUpgradeAction);
        Assert.Equal("Available with Pro—upgrade to enable ad blocking.", vm.AdBlockingStatusText);
    }

    [Fact]
    public async Task Logout_ClearsAdBlockingAccountState()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(true, true, true, "AdBlocking", 15)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        Assert.True(vm.AdBlockingEnabled);

        vm.LogoutCommand.Execute(null);
        await WaitForAsync(() => !vm.IsAuthenticated);

        Assert.False(vm.AdBlockingEnabled);
        Assert.False(vm.IsAdBlockingSettingsAvailable);
        Assert.False(vm.IsAdBlockingEffectivelyEnabled);
        Assert.False(vm.CanToggleAdBlocking);
    }

    [Fact]
    public async Task AccountSwitch_CancelsUnauthorizedAdBlockingRetryFromPreviousAccount()
    {
        var updateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUnauthorized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unauthorizedReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            DnsPreferenceResponse = new DnsPreferenceResponse(false, true, false, "Standard", 15),
            LogoutHandler = (_, _) => Task.FromResult(new ApiMessage { Message = "Logged out." }),
            LoginHandler = (_, _, device, _) => Task.FromResult(new LoginResponse
            {
                Token = "account-b-token",
                RefreshToken = "account-b-refresh",
                Email = "account-b@example.com",
                UserId = "account-b",
                DeviceId = device.DeviceId,
                PlanType = "Pro"
            }),
            UpdateDnsPreferenceHandler = async (_, _) =>
            {
                updateStarted.TrySetResult();
                await releaseUnauthorized.Task;
                unauthorizedReturned.TrySetResult();
                throw new BackendApiException("expired", HttpStatusCode.Unauthorized);
            }
        };
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "account-a-token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "account-a-refresh", CancellationToken.None);
        var vm = CreateViewModel(backend, secretStore);
        await vm.InitializeAsync();

        vm.AdBlockingEnabled = true;
        await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.LogoutCommand.Execute(null);
        await WaitForAsync(() => !vm.IsAuthenticated);
        vm.Email = "account-b@example.com";
        vm.Password = "pass";
        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsAuthenticated && backend.GetDnsPreferenceCalls >= 2 && !vm.AdBlockingEnabled);

        releaseUnauthorized.TrySetResult();
        await unauthorizedReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(1, backend.UpdateDnsPreferenceCalls);
        Assert.True(vm.IsAuthenticated);
        Assert.Equal("account-b@example.com", vm.Email);
        Assert.False(vm.AdBlockingEnabled);
        Assert.False(vm.IsAdBlockingEffectivelyEnabled);
    }

    [Fact]
    public void PasswordPresentationState_TracksVisibilityStrengthAndMatch()
    {
        var vm = CreateViewModel();

        Assert.True(vm.HidePassword);
        vm.TogglePasswordVisibilityCommand.Execute(null);
        Assert.True(vm.ShowPassword);
        Assert.False(vm.HidePassword);

        vm.Password = "weak";
        Assert.True(vm.IsPasswordWeak);
        Assert.False(vm.IsPasswordStrong);

        vm.Password = "Strong1!";
        Assert.Equal(100, vm.PasswordStrengthScore);
        Assert.True(vm.IsPasswordStrong);

        vm.ConfirmPassword = "different";
        Assert.True(vm.ShouldShowPasswordMismatch);

        vm.ConfirmPassword = "Strong1!";
        Assert.True(vm.IsPasswordMatch);
        Assert.False(vm.ShouldShowPasswordMismatch);
    }

    [Fact]
    public void ResetPasswordPresentationState_UsesNewPasswordForMatch()
    {
        var vm = CreateViewModel();

        vm.SelectAuthViewCommand.Execute("Reset");
        vm.NewPassword = "Strong1!";
        vm.ConfirmPassword = "Strong1!";

        Assert.True(vm.IsNewPasswordStrong);
        Assert.True(vm.IsPasswordMatch);
    }

    [Fact]
    public void SubscriptionLimitState_DoesNotInterruptSignedInUi()
    {
        var vm = CreateViewModel();

        vm.Subscription = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 1, false, "Too many devices.");

        Assert.True(vm.IsDeviceLimitReached);
        Assert.False(vm.IsDeviceLimitModalVisible);
        Assert.Equal("Too many devices.", vm.DeviceLimitMessage);
    }

    [Fact]
    public async Task AccountRefresh_DoesNotShowDeviceLimitModal_WhenFreePlanCurrentDeviceUsesOnlySlot()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            SubscriptionStatusResponse = new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 1, false, null),
            DevicesResponse =
            [
                new UserDevice(1, "hash1", "This device", "1.0.0", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, true, true, 0)
            ]
        };
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var vm = CreateViewModel(backend, secretStore);

        await vm.InitializeAsync();

        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.IsDeviceLimitReached);
        Assert.False(vm.IsDeviceLimitModalVisible);
    }

    [Fact]
    public async Task AccountRefresh_DoesNotShowDeviceLimitModal_WhenProPlanCurrentDevicesUseAllSlots()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            SubscriptionStatusResponse = new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 3, 3, false, null),
            DevicesResponse =
            [
                new UserDevice(1, "hash1", "This device", "1.0.0", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, true, true, 0),
                new UserDevice(2, "hash2", "Laptop", "1.0.0", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddHours(-1), true, false, 0),
                new UserDevice(3, "hash3", "Tablet", "1.0.0", DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddHours(-2), true, false, 0)
            ]
        };
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var vm = CreateViewModel(backend, secretStore);

        await vm.InitializeAsync();

        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.IsDeviceLimitReached);
        Assert.False(vm.IsDeviceLimitModalVisible);
    }

    [Fact]
    public async Task LoginAfterLogout_UsesCurrentUsersFreePlanInsteadOfPreviousProPlan()
    {
        var loginResponses = new Queue<LoginResponse>([
            new LoginResponse
            {
                Token = "pro-token",
                RefreshToken = "pro-refresh",
                Email = "pro@example.com",
                UserId = "pro-user",
                DeviceId = "device-pro",
                ActiveDevices = 3,
                MaxDevices = 3,
                PlanType = "Pro"
            },
            new LoginResponse
            {
                Token = "free-token",
                RefreshToken = "free-refresh",
                Email = "free@example.com",
                UserId = "free-user",
                DeviceId = "device-free",
                ActiveDevices = 1,
                MaxDevices = 3,
                PlanType = "Free"
            }
        ]);
        var subscriptionResponses = new Queue<SubscriptionStatus>([
            new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 3, 3, true, null),
            new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null)
        ]);
        var dnsPreferenceResponses = new Queue<DnsPreferenceResponse>([
            new DnsPreferenceResponse(true, true, true, "AdBlocking", 15),
            new DnsPreferenceResponse(false, false, false, "Standard", 15)
        ]);
        var backend = new FakeBackend(tokenValid: true)
        {
            LoginHandler = (email, password, device, cancellationToken) => Task.FromResult(loginResponses.Dequeue()),
            LogoutHandler = (refreshToken, cancellationToken) => Task.FromResult(new ApiMessage { Message = "Logged out." }),
            SubscriptionStatusHandler = cancellationToken => Task.FromResult(subscriptionResponses.Dequeue()),
            DnsPreferenceHandler = cancellationToken => Task.FromResult(dnsPreferenceResponses.Dequeue())
        };
        var vm = CreateViewModel(backend);

        vm.Email = "pro@example.com";
        vm.Password = "pass";
        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsAuthenticated && backend.GetSubscriptionStatusCalls == 1 && backend.GetDnsPreferenceCalls == 1 && vm.AdBlockingEnabled);

        Assert.Equal("Pro", vm.PlanText);
        Assert.True(vm.IsProPlan);
        Assert.True(vm.AdBlockingEnabled);
        Assert.True(vm.IsAdBlockingEffectivelyEnabled);

        vm.Password = "password-before-logout";
        vm.ConfirmPassword = "confirm-before-logout";
        vm.ResetToken = "reset-token-before-logout";
        vm.NewPassword = "new-password-before-logout";
        vm.OAuthToken = "oauth-token-before-logout";
        vm.TwoFactorManagementCode = "2fa-management-before-logout";
        vm.TwoFactorSharedKey = "shared-key-before-logout";
        vm.TwoFactorAuthenticatorUri = "otpauth://before-logout";
        vm.RecoveryCodesText = "recovery-before-logout";
        vm.RegisteredUserId = "registered-before-logout";

        vm.LogoutCommand.Execute(null);
        await WaitForAsync(() => !vm.IsAuthenticated && vm.PlanText == "Free");

        Assert.Empty(vm.Email);
        Assert.Empty(vm.Password);
        Assert.Empty(vm.ConfirmPassword);
        Assert.Empty(vm.ResetToken);
        Assert.Empty(vm.NewPassword);
        Assert.Empty(vm.OAuthToken);
        Assert.Empty(vm.TwoFactorManagementCode);
        Assert.Empty(vm.TwoFactorSharedKey);
        Assert.Empty(vm.TwoFactorAuthenticatorUri);
        Assert.Empty(vm.RecoveryCodesText);
        Assert.Empty(vm.RegisteredUserId);
        Assert.False(vm.AdBlockingEnabled);
        Assert.False(vm.IsAdBlockingSettingsAvailable);
        Assert.False(vm.IsAdBlockingEffectivelyEnabled);

        vm.Email = "free@example.com";
        vm.Password = "pass";
        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsAuthenticated && backend.GetSubscriptionStatusCalls == 2 && backend.GetDnsPreferenceCalls == 2 && vm.IsAdBlockingSettingsAvailable && !vm.AdBlockingEnabled);

        Assert.Equal("Free", vm.PlanText);
        Assert.False(vm.IsProPlan);
        Assert.True(vm.IsFreePlan);
        Assert.True(vm.ShowUpgradeSettingsCard);
        Assert.False(vm.CanAccessCertificates);
        Assert.False(vm.AdBlockingEnabled);
        Assert.False(vm.IsAdBlockingEffectivelyEnabled);
        Assert.False(vm.CanToggleAdBlocking);
    }

    [Fact]
    public async Task StaleProAccountRefresh_DoesNotOverwriteLaterFreeLoginState()
    {
        var loginResponses = new Queue<LoginResponse>([
            new LoginResponse
            {
                Token = "pro-token",
                RefreshToken = "pro-refresh",
                Email = "pro@example.com",
                UserId = "pro-user",
                DeviceId = "device-pro",
                ActiveDevices = 3,
                MaxDevices = 3,
                PlanType = "Pro"
            },
            new LoginResponse
            {
                Token = "free-token",
                RefreshToken = "free-refresh",
                Email = "free@example.com",
                UserId = "free-user",
                DeviceId = "device-free",
                ActiveDevices = 1,
                MaxDevices = 3,
                PlanType = "Free"
            }
        ]);
        var subscriptionResponses = new Queue<SubscriptionStatus>([
            new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 3, 3, true, null),
            new SubscriptionStatus("Free", false, "Active", null, "Monthly", 1, 3, true, null),
            new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 3, 3, true, null)
        ]);
        var staleRefreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usageQuotaCallCount = 0;
        var backend = new FakeBackend(tokenValid: true)
        {
            LoginHandler = (email, password, device, cancellationToken) => Task.FromResult(loginResponses.Dequeue()),
            LogoutHandler = (refreshToken, cancellationToken) => Task.FromResult(new ApiMessage { Message = "Logged out." }),
            SubscriptionStatusHandler = cancellationToken => Task.FromResult(subscriptionResponses.Dequeue()),
            UsageQuotaHandler = async cancellationToken =>
            {
                var callNumber = Interlocked.Increment(ref usageQuotaCallCount);
                if (callNumber == 2)
                {
                    staleRefreshEntered.TrySetResult();
                    await releaseStaleRefresh.Task;
                }

                return new UsageQuota { BytesUsed = 0, BytesLimit = null, IsUnlimited = true };
            }
        };
        var vm = CreateViewModel(backend);

        vm.Email = "pro@example.com";
        vm.Password = "pass";
        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsAuthenticated && backend.GetSubscriptionStatusCalls == 1);

        var staleRefreshTask = vm.RefreshCurrentAccountStateAsync();
        await staleRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.LogoutCommand.Execute(null);
        await WaitForAsync(() => !vm.IsAuthenticated && vm.PlanText == "Free");

        vm.Email = "free@example.com";
        vm.Password = "pass";
        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsAuthenticated && backend.GetSubscriptionStatusCalls == 2);

        releaseStaleRefresh.TrySetResult();
        await staleRefreshTask;
        await WaitForAsync(() => backend.GetSubscriptionStatusCalls == 3);

        Assert.Equal("Free", vm.PlanText);
        Assert.False(vm.IsProPlan);
        Assert.True(vm.IsFreePlan);
        Assert.True(vm.ShowUpgradeSettingsCard);
        Assert.False(vm.CanAccessCertificates);
    }

    [Fact]
    public async Task RemoveSelectedDevice_OnlyAllowsActiveNonCurrentDevices()
    {
        var backend = new FakeBackend(tokenValid: true);
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh", CancellationToken.None);
        var vm = CreateViewModel(backend, secretStore);
        await vm.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var current = new UserDevice(1, "hash1", "This device", "1.0.0", now.AddDays(-1), now, true, true, 0);
        var inactive = new UserDevice(2, "hash2", "Old laptop", "1.0.0", now.AddDays(-10), now.AddDays(-3), false, false, 3);
        var otherActive = new UserDevice(3, "hash3", "Phone", "1.0.0", now.AddDays(-2), now.AddHours(-1), true, false, 0);

        vm.SelectedDevice = current;
        Assert.False(vm.RemoveSelectedDeviceCommand.CanExecute(null));
        vm.RemoveSelectedDeviceCommand.Execute(null);
        Assert.Equal(0, backend.RemoveDeviceCalls);

        vm.SelectedDevice = inactive;
        Assert.False(vm.RemoveSelectedDeviceCommand.CanExecute(null));
        vm.RemoveSelectedDeviceCommand.Execute(null);
        Assert.Equal(0, backend.RemoveDeviceCalls);

        vm.SelectedDevice = otherActive;
        Assert.True(vm.RemoveSelectedDeviceCommand.CanExecute(null));
        vm.RemoveSelectedDeviceCommand.Execute(null);
        await WaitForAsync(() => backend.RemoveDeviceCalls == 1);
        Assert.Equal(otherActive.Id, backend.LastRemovedDeviceId);
    }

    [Fact]
    public async Task OAuthLoginDeviceLimit_ShowsPreAuthRemovalModalAndRemovesSelectedDeviceWithCode()
    {
        var limit = new DeviceLimitExceededResponse
        {
            Message = "Device limit reached. You have 1 active device(s).",
            ErrorCode = "DEVICE_LIMIT_EXCEEDED",
            CurrentDevices = 1,
            MaxDevices = 1,
            PlanType = "Free",
            Devices =
            [
                new UserDevice(42, "hash42", "Existing device", "1.0.0", null, DateTimeOffset.UtcNow, false, false, 0)
            ]
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            GoogleCodeLoginException = new BackendApiException(
                limit.Message!,
                HttpStatusCode.Conflict,
                JsonSerializer.Serialize(limit, JsonOptions.Default))
        };
        var vm = CreateViewModel(backend);

        vm.OAuthLoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsDeviceLimitModalVisible);

        Assert.False(vm.IsAuthenticated);
        Assert.Equal("Device limit reached. You have 1 active device(s).", vm.StatusMessage);
        Assert.Single(vm.Devices);
        Assert.True(vm.Devices[0].IsActive);
        Assert.Equal(42, vm.SelectedDevice?.Id);

        vm.RemoveSelectedDeviceCommand.Execute(null);
        await WaitForAsync(() => backend.PreAuthOAuthCodeRemoveCalls == 1);

        Assert.Equal("Google", backend.LastPreAuthOAuthCodeProvider);
        Assert.Equal("authorization-code", backend.LastPreAuthOAuthAuthorizationCode?.Code);
        Assert.Equal(42, backend.LastPreAuthOAuthCodeDeviceId);
        Assert.False(vm.IsAuthenticated);
        Assert.False(vm.IsDeviceLimitModalVisible);
    }

    [Fact]
    public async Task PasswordLoginDeviceLimit_ShowsPreAuthRemovalModalAndRemovesSelectedDevice()
    {
        var limit = new DeviceLimitExceededResponse
        {
            Message = "Device limit reached. You have 1 active device(s).",
            ErrorCode = "DEVICE_LIMIT_EXCEEDED",
            CurrentDevices = 1,
            MaxDevices = 1,
            PlanType = "Free",
            Devices =
            [
                new UserDevice(42, "hash42", "Existing device", "1.0.0", null, DateTimeOffset.UtcNow, false, false, 0)
            ]
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            LoginException = new BackendApiException(
                limit.Message!,
                HttpStatusCode.Conflict,
                JsonSerializer.Serialize(limit, JsonOptions.Default))
        };
        var vm = CreateViewModel(backend);
        vm.Email = "user@example.com";
        vm.Password = "pass";

        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsDeviceLimitModalVisible);

        Assert.False(vm.IsAuthenticated);
        Assert.Equal("Device limit reached. You have 1 active device(s).", vm.StatusMessage);
        Assert.Single(vm.Devices);
        Assert.True(vm.Devices[0].IsActive);
        Assert.Equal(42, vm.SelectedDevice?.Id);

        vm.RemoveSelectedDeviceCommand.Execute(null);
        await WaitForAsync(() => backend.PreAuthRemoveCalls == 1);

        Assert.Equal("user@example.com", backend.LastPreAuthRemoveEmail);
        Assert.Equal("pass", backend.LastPreAuthRemovePassword);
        Assert.Equal(42, backend.LastPreAuthRemoveDeviceId);
        Assert.False(vm.IsAuthenticated);
        Assert.False(vm.IsDeviceLimitModalVisible);
    }

    [Fact]
    public async Task DismissDeviceLimit_ClearsUnauthenticatedPreAuthDeviceState()
    {
        var limit = new DeviceLimitExceededResponse
        {
            Message = "Device limit reached. You have 1 active device(s).",
            ErrorCode = "DEVICE_LIMIT_EXCEEDED",
            CurrentDevices = 1,
            MaxDevices = 1,
            PlanType = "Free",
            Devices =
            [
                new UserDevice(42, "hash42", "Existing device", "1.0.0", null, DateTimeOffset.UtcNow, false, false, 0)
            ]
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            LoginException = new BackendApiException(
                limit.Message!,
                HttpStatusCode.Conflict,
                JsonSerializer.Serialize(limit, JsonOptions.Default))
        };
        var vm = CreateViewModel(backend);
        vm.Email = "user@example.com";
        vm.Password = "pass";

        vm.LoginCommand.Execute(null);
        await WaitForAsync(() => vm.IsDeviceLimitModalVisible);

        vm.DismissDeviceLimitCommand.Execute(null);

        Assert.False(vm.IsDeviceLimitModalVisible);
        Assert.Empty(vm.Devices);
        Assert.Null(vm.SelectedDevice);
    }

    [Fact]
    public async Task VerifyTwoFactorDeviceLimit_ShowsPreAuthRemovalModalAndRemovesSelectedDevice()
    {
        var limit = new DeviceLimitExceededResponse
        {
            Message = "Device limit reached. You have 1 active device(s).",
            ErrorCode = "DEVICE_LIMIT_EXCEEDED",
            CurrentDevices = 1,
            MaxDevices = 1,
            PlanType = "Free",
            Devices =
            [
                new UserDevice(42, "hash42", "Existing device", "1.0.0", null, DateTimeOffset.UtcNow, false, false, 0)
            ]
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            VerifyTwoFactorException = new BackendApiException(
                limit.Message!,
                HttpStatusCode.Conflict,
                JsonSerializer.Serialize(limit, JsonOptions.Default))
        };
        var vm = CreateViewModel(backend);
        vm.Email = "user@example.com";
        vm.Password = "pass";
        typeof(MainViewModel).GetField("_pendingLoginToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, "pending-login-token");

        vm.VerifyTwoFactorCommand.Execute(null);
        await WaitForAsync(() => vm.IsDeviceLimitModalVisible);

        Assert.Single(vm.Devices);
        vm.RemoveSelectedDeviceCommand.Execute(null);
        await WaitForAsync(() => backend.PreAuthRemoveCalls == 1);
    }

    [Fact]
    public void BillingCycleSelection_UpdatesSegmentState()
    {
        var vm = CreateViewModel();

        Assert.True(vm.IsMonthlyBilling);

        vm.SelectBillingCycleCommand.Execute("yearly");

        Assert.True(vm.IsYearlyBilling);
        Assert.False(vm.IsMonthlyBilling);
    }

    [Fact]
    public async Task OpenUpgrade_NavigatesToUpgradePage()
    {
        var vm = CreateViewModel();

        vm.OpenUpgradeCommand.Execute(null);
        await WaitForAsync(() => vm.IsUpgrade);

        Assert.Equal("Upgrade", vm.CurrentSection);
    }

    [Fact]
    public async Task GoBackToSettings_ReturnsFromUpgradePage()
    {
        var vm = CreateViewModel();
        vm.OpenUpgradeCommand.Execute(null);
        await WaitForAsync(() => vm.IsUpgrade);

        vm.GoBackToSettingsCommand.Execute(null);

        Assert.True(vm.IsSettings);
        Assert.Equal("Settings", vm.CurrentSection);
    }

    [Fact]
    public async Task SelectCard_CreatesCheckoutAndOpensIntegratedCheckout()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService();
        var backend = new FakeBackend(tokenValid: true)
        {
            CardCheckoutResponse = new CardCheckoutResponse(
                "https://checkout.example/pro",
                "ch_123",
                42,
                "Yearly",
                29.99m,
                "USD",
                "prod_yearly",
                "user@example.com",
                "card-42")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);
        vm.SelectBillingCycleCommand.Execute("yearly");

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);

        Assert.Equal(BillingCycle.Yearly, backend.LastCheckoutCycle);
        Assert.Equal("https://checkout.example/pro", vm.CheckoutUrl);
        Assert.True(vm.IsCardSelected);
        Assert.True(vm.HasCheckoutUrl);
        Assert.True(vm.IsCardCheckoutLinkVisible);
        Assert.False(vm.IsPaymentMethodSelectionVisible);
        Assert.Equal("https://checkout.example/pro", checkoutWindow.LastRequest?.CheckoutUrl);
        Assert.Equal("ch_123", checkoutWindow.LastRequest?.TransactionId);
        Assert.Equal("Yearly", checkoutWindow.LastRequest?.BillingCycle);
    }

    [Fact]
    public async Task SelectCard_WhenCheckoutRemainsOpen_StopsLoadingAfterWindowLaunch()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            ShowCompletion = new TaskCompletionSource<CardCheckoutWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var backend = new FakeBackend(tokenValid: true);
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);

        Assert.False(vm.IsLoadingPayment);
        Assert.False(vm.IsCardCheckoutLinkVisible);
        Assert.True(((AsyncCommand)vm.SelectCardCommand).IsRunning);

        checkoutWindow.ShowCompletion.TrySetResult(CardCheckoutWindowResult.Closed);
        await WaitForAsync(() => !((AsyncCommand)vm.SelectCardCommand).IsRunning);
        Assert.True(vm.IsCardCheckoutLinkVisible);
    }

    [Fact]
    public async Task SelectCard_WhenCheckoutUrlMissing_ShowsClearStatus()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService();
        var backend = new FakeBackend(tokenValid: true)
        {
            CardCheckoutResponse = new CardCheckoutResponse(null, "ch_123", 42, "Monthly", 5.99m, "USD", "prod_monthly", "user@example.com", "card-42")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => backend.CreateCardCheckoutCalls == 1);

        Assert.Equal("Checkout URL was not returned by the backend.", vm.StatusMessage);
        Assert.False(vm.HasCheckoutUrl);
        Assert.Equal(0, checkoutWindow.ShowCalls);
    }

    [Fact]
    public async Task SelectCard_WhenIntegratedCheckoutUnavailable_LeavesBrowserFallbackVisible()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            Result = CardCheckoutWindowResult.Unavailable
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            CardCheckoutResponse = new CardCheckoutResponse("https://checkout.example/pro", "ch_123", 42, "Monthly", 5.99m, "USD", "prod_monthly", "user@example.com", "card-42")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);

        Assert.True(vm.IsCardSelected);
        Assert.True(vm.HasCheckoutUrl);
        Assert.Equal("https://checkout.example/pro", vm.CheckoutUrl);
        Assert.Contains("In-app checkout is unavailable", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectCard_WhenPaymentIsConfirmed_RefreshesAndReturnsToSettings()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            Result = CardCheckoutWindowResult.Paid
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            CardCheckoutResponse = new CardCheckoutResponse("https://checkout.example/pro", "ch_123", 42, "Monthly", 5.99m, "USD", "prod_monthly", "user@example.com", "card-42")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);
        backend.ResetAccountStateCallCounts();

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => vm.IsPaymentComplete && backend.GetSubscriptionStatusCalls == 1);

        Assert.True(vm.IsSettings);
        Assert.Contains("Payment confirmed", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenCardCheckoutInBrowser_WhenBrowserLaunchFails_LeavesCheckoutUrlVisible()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            BrowserLaunchResult = new ExternalUriLaunchResult(false, "No default browser is configured.")
        };
        var backend = new FakeBackend(tokenValid: true)
        {
            CardCheckoutResponse = new CardCheckoutResponse("https://checkout.example/pro", "ch_123", 42, "Monthly", 5.99m, "USD", "prod_monthly", "user@example.com", "card-42")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);
        vm.OpenCardCheckoutInBrowserCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.BrowserOpenCalls == 1);

        Assert.True(vm.HasCheckoutUrl);
        Assert.Equal("https://checkout.example/pro", vm.CheckoutUrl);
        Assert.Equal("Browser checkout could not be opened automatically. Try again or use another browser.", vm.StatusMessage);
    }

    [Fact]
    public async Task BrowserCheckout_WhenPaid_RefreshesSubscriptionStateAutomatically()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            CardCheckoutResponse = new CardCheckoutResponse("https://checkout.example/pro", "ch_123", 42, "Monthly", 5.99m, "USD", "prod_monthly", "user@example.com", "card-42")
        };
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            MonitorResult = CardCheckoutWindowResult.Paid
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);
        backend.ResetAccountStateCallCounts();

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);

        vm.OpenCardCheckoutInBrowserCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.MonitorCalls == 1 && backend.GetSubscriptionStatusCalls == 1);

        Assert.Equal(1, backend.GetSubscriptionStatusCalls);
    }

    [Fact]
    public async Task BrowserCheckout_WhenIntegratedCheckoutIsActive_DoesNotStartSecondMonitor()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            IsCheckoutActive = true
        };
        var backend = new FakeBackend(tokenValid: true);
        var vm = await CreateAuthenticatedViewModelAsync(backend, cardCheckoutWindow: checkoutWindow);

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);

        vm.OpenCardCheckoutInBrowserCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.BrowserOpenCalls == 1);

        Assert.Equal(0, checkoutWindow.MonitorCalls);
        Assert.Equal("https://checkout.example/pro", checkoutWindow.LastBrowserUrl);
        Assert.Contains("monitoring payment automatically", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwitchPaymentMethod_CancelsOpenCardCheckoutSession()
    {
        var checkoutWindow = new FakeCardCheckoutWindowService
        {
            ShowCompletion = new TaskCompletionSource<CardCheckoutWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var vm = await CreateAuthenticatedViewModelAsync(new FakeBackend(tokenValid: true), cardCheckoutWindow: checkoutWindow);

        vm.SelectCardCommand.Execute(null);
        await WaitForAsync(() => checkoutWindow.ShowCalls == 1);
        var cancelCallsBeforeSwitch = checkoutWindow.CancelCalls;

        vm.SwitchPaymentMethodCommand.Execute(null);

        Assert.Equal(cancelCallsBeforeSwitch + 1, checkoutWindow.CancelCalls);
        Assert.True(vm.IsPaymentMethodSelectionVisible);
        Assert.False(vm.IsLoadingPayment);

        checkoutWindow.ShowCompletion.TrySetResult(CardCheckoutWindowResult.Closed);
        await WaitForAsync(() => !((AsyncCommand)vm.SelectCardCommand).IsRunning);
    }

    [Fact]
    public async Task SelectMonero_CreatesInvoiceAndShowsStatus()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            MoneroPriceResponse = new MoneroPriceResponse(0.04m, 5.99m, 149.75m, "XMR", "LibreGuard Pro"),
            MoneroInvoiceResponse = new MoneroInvoiceResponse("invoice-1", "xmr-address", 0.04m, "XMR", "Pending", "LibreGuard Pro", DateTimeOffset.UtcNow, "Monthly"),
            MoneroStatusResponse = new MoneroStatusResponse("invoice-1", "Pending", 0.04m, 0.01m, 2, 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), "Monthly")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.SelectMoneroCommand.Execute(null);
        await WaitForAsync(() => backend.CreateMoneroInvoiceCalls == 1 && vm.MoneroStatus is not null);

        Assert.True(vm.IsMoneroSelected);
        Assert.False(vm.IsPaymentMethodSelectionVisible);
        Assert.Equal("invoice-1", vm.MoneroInvoice?.InvoiceId);
        Assert.Equal(0.03m, vm.Shortfall);
        Assert.Equal("2/10 Confirmations", vm.MoneroConfirmationsText);
    }

    [Fact]
    public async Task CompletedMoneroPayment_RefreshesSubscription()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            MoneroInvoiceResponse = new MoneroInvoiceResponse("invoice-1", "xmr-address", 0.04m, "XMR", "Pending", "LibreGuard Pro", DateTimeOffset.UtcNow, "Monthly"),
            MoneroStatusResponse = new MoneroStatusResponse("invoice-1", "Confirmed", 0.04m, 0.04m, 10, 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), "Monthly"),
            SubscriptionStatusResponse = new SubscriptionStatus("Pro", true, "Active", null, "Monthly", 1, 3, true, null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        vm.MoneroInvoice = backend.MoneroInvoiceResponse;

        vm.CheckPaymentStatusCommand.Execute(null);
        await WaitForAsync(() => vm.IsPaymentComplete && backend.GetSubscriptionStatusCalls > 0);

        Assert.True(vm.IsProPlan);
        Assert.Equal("Pro", vm.PlanText);
    }

    [Fact]
    public async Task SwitchPaymentMethod_ClearsMoneroState()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            MoneroInvoiceResponse = new MoneroInvoiceResponse("invoice-1", "xmr-address", 0.04m, "XMR", "Pending", "LibreGuard Pro", DateTimeOffset.UtcNow, "Monthly")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        vm.SelectMoneroCommand.Execute(null);
        await WaitForAsync(() => vm.MoneroInvoice is not null);

        vm.SwitchPaymentMethodCommand.Execute(null);

        Assert.False(vm.IsMoneroSelected);
        Assert.True(vm.IsPaymentMethodSelectionVisible);
        Assert.Null(vm.MoneroInvoice);
        Assert.Null(vm.MoneroStatus);
        Assert.Equal(0, vm.Shortfall);
    }

    [Fact]
    public async Task CopyMoneroCommands_WriteClipboard()
    {
        var clipboard = new FakeClipboardService();
        var vm = CreateViewModel(clipboard: clipboard);
        vm.MoneroPrice = new MoneroPriceResponse(0.04m, 5.99m, 149.75m, "XMR", "LibreGuard Pro");
        vm.MoneroInvoice = new MoneroInvoiceResponse("invoice-1", "xmr-address", 0.04m, "XMR", "Pending", "LibreGuard Pro", DateTimeOffset.UtcNow, "Monthly");

        vm.CopyAmountCommand.Execute(null);
        await WaitForAsync(() => clipboard.Text == "0.04");
        vm.CopyAddressCommand.Execute(null);
        await WaitForAsync(() => clipboard.Text == "xmr-address");

        Assert.Equal("xmr-address", clipboard.Text);
    }

    [Theory]
    [InlineData("Devices")]
    [InlineData("Certificates")]
    public async Task EnteringAccountSection_RefreshesAccountStateAutomatically(string section)
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null),
            CertificatesResponse = new[]
            {
                new UserCertificate { Id = 7, Name = "Laptop", VpnType = "OpenVPN", ServerName = "Amsterdam", ServerIp = "10.0.0.1", IsRevoked = false }
            }
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        backend.ResetAccountStateCallCounts();

        vm.SelectSectionCommand.Execute(section);

        await WaitForAsync(() => backend.GetCertificatesCalls == 1);

        Assert.Equal(1, backend.GetServersCalls);
        Assert.Equal(1, backend.GetUsageQuotaCalls);
        Assert.Equal(1, backend.GetSubscriptionStatusCalls);
        Assert.Equal(1, backend.GetDevicesCalls);
        Assert.Equal(1, backend.GetTwoFactorStatusCalls);
        Assert.Equal(1, backend.GetCertificatesCalls);
        Assert.True(vm.TwoFactorEnabled);
    }

    [Fact]
    public async Task EnteringCertificatesSection_AsPro_LoadsCertificatesAndEnablesDownloads()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null),
            CertificatesResponse = new[]
            {
                new UserCertificate { Id = 7, Name = "Laptop", VpnType = "OpenVPN", ServerName = "Amsterdam", ServerIp = "10.0.0.1", IsRevoked = false }
            }
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        backend.ResetAccountStateCallCounts();

        vm.SelectSectionCommand.Execute("Certificates");

        await WaitForAsync(() => backend.GetCertificatesCalls == 1);

        Assert.True(vm.CanAccessCertificates);
        Assert.False(vm.ShowCertificatesUpgradePrompt);
        Assert.Single(vm.Certificates);
        Assert.Equal(7, vm.Certificates[0].Id);
    }

    [Fact]
    public async Task EnteringCertificatesSection_AsNonPro_DoesNotLoadCertificatesAndShowsUpgradePrompt()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: false)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null),
            CertificatesResponse = new[]
            {
                new UserCertificate { Id = 7, Name = "Laptop", VpnType = "OpenVPN", ServerName = "Amsterdam", ServerIp = "10.0.0.1", IsRevoked = false }
            }
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        vm.Certificates.Add(new UserCertificate { Id = 99, Name = "Stale", VpnType = "IKEV2/IPSec", ServerName = "Berlin", ServerIp = "10.0.0.9", IsRevoked = false });
        vm.SelectedCertificate = vm.Certificates[0];
        backend.ResetAccountStateCallCounts();

        vm.SelectSectionCommand.Execute("Certificates");

        await WaitForAsync(() => backend.GetSubscriptionStatusCalls == 1 && vm.ShowCertificatesUpgradePrompt);

        Assert.Equal(0, backend.GetCertificatesCalls);
        Assert.False(vm.CanAccessCertificates);
        Assert.True(vm.ShowCertificatesUpgradePrompt);
        Assert.Empty(vm.Certificates);
        Assert.Null(vm.SelectedCertificate);
    }

    [Fact]
    public async Task DownloadSelectedConfigCommand_UsesClickedCertificateId()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true);
        using var savePicker = new FakeFileSavePickerService();
        var vm = await CreateAuthenticatedViewModelAsync(backend, savePicker);
        var certificate = new UserCertificate { Id = 14, Name = "Laptop", VpnType = "OpenVPN", ServerName = "Amsterdam", ServerIp = "10.0.0.1", IsRevoked = false };

        vm.DownloadSelectedConfigCommand.Execute(certificate);

        await WaitForAsync(() => backend.LastDownloadedCertificateConfigId == certificate.Id);
        await WaitForAsync(() => vm.StatusMessage.StartsWith("Saved ", StringComparison.Ordinal));

        Assert.Equal(certificate.Id, backend.LastDownloadedCertificateConfigId);
        Assert.Equal("Laptop-config.ovpn", savePicker.LastSuggestedFileName);
        var savedPath = Path.Combine(savePicker.DirectoryPath, "Laptop-config.ovpn");
        await WaitForAsync(() => CanOpenExclusive(savedPath));
        Assert.True(File.Exists(savedPath));
    }

    [Fact]
    public async Task DownloadSelectedCertificateCommand_UsesClickedCertificateId()
    {
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true);
        using var savePicker = new FakeFileSavePickerService();
        var vm = await CreateAuthenticatedViewModelAsync(backend, savePicker);
        var certificate = new UserCertificate { Id = 21, Name = "Laptop", VpnType = "OpenVPN", ServerName = "Amsterdam", ServerIp = "10.0.0.1", IsRevoked = false };

        vm.DownloadSelectedCertificateCommand.Execute(certificate);

        await WaitForAsync(() => backend.LastDownloadedCertificateId == certificate.Id);
        await WaitForAsync(() => vm.StatusMessage.StartsWith("Saved ", StringComparison.Ordinal));

        Assert.Equal(certificate.Id, backend.LastDownloadedCertificateId);
        Assert.Equal("Laptop.crt", savePicker.LastSuggestedFileName);
        var savedPath = Path.Combine(savePicker.DirectoryPath, "Laptop.crt");
        await WaitForAsync(() => CanOpenExclusive(savedPath));
        Assert.True(File.Exists(savedPath));
    }

    [Fact]
    public async Task AccountSectionRefresh_DoesNotStackDuplicateRequests()
    {
        var quotaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quotaRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quotaCallCount = 0;
        var backend = new FakeBackend(tokenValid: true, subscriptionIsPro: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null),
            UsageQuotaHandler = async cancellationToken =>
            {
                if (Interlocked.Increment(ref quotaCallCount) == 2)
                {
                    quotaEntered.TrySetResult();
                    await quotaRelease.Task.WaitAsync(cancellationToken);
                }

                return new UsageQuota { BytesUsed = 0, BytesLimit = null, IsUnlimited = true };
            }
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        backend.ResetAccountStateCallCounts();

        vm.SelectSectionCommand.Execute("Devices");
        await quotaEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.SelectSectionCommand.Execute("Certificates");

        Assert.Equal(1, backend.GetServersCalls);
        Assert.Equal(1, backend.GetUsageQuotaCalls);

        quotaRelease.TrySetResult();
        await WaitForAsync(() => backend.GetCertificatesCalls == 1);

        Assert.Equal(1, backend.GetSubscriptionStatusCalls);
        Assert.Equal(1, backend.GetDevicesCalls);
        Assert.Equal(1, backend.GetTwoFactorStatusCalls);
        Assert.Equal(1, backend.GetCertificatesCalls);
    }

    [Fact]
    public async Task InitializeAsync_SyncsTwoFactorToggleWithoutOpeningDialogs()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        var setupRequested = 0;
        var disableRequested = 0;
        vm.TwoFactorSetupDialogRequested += (_, _) => setupRequested++;
        vm.TwoFactorDisableDialogRequested += (_, _) => disableRequested++;

        await vm.RefreshCurrentAccountStateAsync();

        Assert.True(vm.TwoFactorEnabled);
        Assert.True(vm.TwoFactorToggleEnabled);
        Assert.Equal(0, setupRequested);
        Assert.Equal(0, disableRequested);
    }

    [Fact]
    public async Task TwoFactorToggleOn_StartsSetupAndRequestsDialog()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorSetupResponse = new TwoFactorSetup("AAAA BBBB", "otpauth://totp/libreguard", "AAAABBBB", null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        var setupRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TwoFactorSetupDialogRequested += (_, _) => setupRequested.TrySetResult();

        vm.TwoFactorToggleEnabled = true;
        await setupRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, backend.SetupTwoFactorCalls);
        Assert.True(vm.TwoFactorToggleEnabled);
        Assert.Equal("AAAA BBBB", vm.TwoFactorSharedKey);
        Assert.Equal("otpauth://totp/libreguard", vm.TwoFactorAuthenticatorUri);
    }

    [Fact]
    public async Task CancelTwoFactorSetupFlow_RestoresToggleOff()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorSetupResponse = new TwoFactorSetup("AAAA BBBB", "otpauth://totp/libreguard", "AAAABBBB", null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        var setupRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TwoFactorSetupDialogRequested += (_, _) => setupRequested.TrySetResult();

        vm.TwoFactorToggleEnabled = true;
        await setupRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.CancelTwoFactorSetupFlow();

        Assert.False(vm.TwoFactorToggleEnabled);
        Assert.Equal(string.Empty, vm.TwoFactorSharedKey);
        Assert.Equal(string.Empty, vm.TwoFactorAuthenticatorUri);
    }

    [Fact]
    public async Task SetupDialog_EnableCallsApiAndCloses()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorSetupResponse = new TwoFactorSetup("AAAA BBBB", "otpauth://totp/libreguard", "AAAABBBB", null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        var setupRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TwoFactorSetupDialogRequested += (_, _) => setupRequested.TrySetResult();

        vm.TwoFactorToggleEnabled = true;
        await setupRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dialogVm = vm.CreateTwoFactorSetupDialogViewModel();
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialogVm.CloseRequested += (_, result) => closed.TrySetResult(result);
        dialogVm.VerificationCode = "123456";

        dialogVm.EnableTwoFactorCommand.Execute(null);
        var result = await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result);
        Assert.Equal(1, backend.EnableTwoFactorCalls);
        Assert.Equal("123456", backend.LastEnabledTwoFactorCode);
    }

    [Fact]
    public async Task TwoFactorToggleOff_RequestsDisableDialog()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        var disableRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.TwoFactorDisableDialogRequested += (_, _) => disableRequested.TrySetResult();

        vm.TwoFactorToggleEnabled = false;
        await disableRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, backend.DisableTwoFactorCalls);
    }

    [Fact]
    public async Task CancelTwoFactorDisableFlow_RestoresToggleOn()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.TwoFactorToggleEnabled = false;
        await WaitForAsync(() => !vm.TwoFactorToggleEnabled);
        vm.CancelTwoFactorDisableFlow();

        Assert.True(vm.TwoFactorToggleEnabled);
    }

    [Fact]
    public async Task ConfirmTwoFactorDisableAsync_DisablesAndRefreshesState()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null)
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        vm.TwoFactorToggleEnabled = false;

        var success = await vm.ConfirmTwoFactorDisableAsync(CancellationToken.None);

        Assert.True(success);
        Assert.Equal(1, backend.DisableTwoFactorCalls);
        Assert.False(vm.TwoFactorEnabled);
        Assert.False(vm.TwoFactorToggleEnabled);
    }

    [Fact]
    public async Task TwoFactorSetupFailure_RestoresToggleAndShowsError()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            SetupTwoFactorException = new InvalidOperationException("setup failed")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);

        vm.TwoFactorToggleEnabled = true;
        await WaitForAsync(() => backend.SetupTwoFactorCalls == 1);

        Assert.False(vm.TwoFactorToggleEnabled);
        Assert.Equal("setup failed", vm.StatusMessage);
    }

    [Fact]
    public async Task ConfirmTwoFactorDisableFailure_RestoresToggleAndShowsError()
    {
        var backend = new FakeBackend(tokenValid: true)
        {
            TwoFactorStatusResponse = new TwoFactorStatus(true, true, 8, null),
            DisableTwoFactorException = new InvalidOperationException("disable failed")
        };
        var vm = await CreateAuthenticatedViewModelAsync(backend);
        vm.TwoFactorToggleEnabled = false;

        var success = await vm.ConfirmTwoFactorDisableAsync(CancellationToken.None);

        Assert.False(success);
        Assert.True(vm.TwoFactorToggleEnabled);
        Assert.Equal("disable failed", vm.StatusMessage);
    }

    [Fact]
    public async Task LoginCommandRunningState_CanBeCancelled()
    {
        var loginStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            LoginStarted = loginStarted,
            LoginReleased = loginReleased
        };
        var vm = CreateViewModel(backend);

        vm.LoginCommand.Execute(null);
        await loginStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(vm.IsLoginRunning);
        Assert.False(vm.IsLoginIdle);

        vm.CancelLoginCommand.Execute(null);
        await loginReleased.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => vm.IsLoginIdle);

        Assert.False(vm.IsLoginRunning);
        Assert.True(vm.IsLoginIdle);
    }

    private static MainViewModel CreateViewModel(
        FakeBackend? backend = null,
        InMemorySecretStore? secretStore = null,
        FakeVpnConnectionService? vpn = null,
        IServerLatencyService? latencyService = null,
        FakeTunnelTrafficMonitor? tunnelTrafficMonitor = null,
        FakeGoogleOAuthService? googleOAuth = null,
        InMemorySettingsStore? settingsStore = null,
        IFileSavePickerService? fileSavePicker = null,
        IClipboardService? clipboard = null,
        ICardCheckoutWindowService? cardCheckoutWindow = null,
        FakeDesktopNotificationService? desktopNotifications = null)
    {
        backend ??= new FakeBackend();
        secretStore ??= new InMemorySecretStore();
        vpn ??= new FakeVpnConnectionService();
        latencyService ??= new FakeLatencyService();
        tunnelTrafficMonitor ??= new FakeTunnelTrafficMonitor();
        settingsStore ??= new InMemorySettingsStore();
        var deviceIdentity = new FakeDeviceIdentityService();
        var vm = new MainViewModel(
            new AuthSessionService(backend, deviceIdentity, secretStore),
            backend,
            deviceIdentity,
            vpn,
            settingsStore,
            new LocalStatisticsStore(settingsStore),
            new FakeThemePreferenceService(),
            cardCheckoutWindow ?? new FakeCardCheckoutWindowService(),
            googleOAuth ?? new FakeGoogleOAuthService(),
            new FakePreflightService(),
            latencyService,
            tunnelTrafficMonitor,
            fileSavePicker ?? new FakeFileSavePickerService(),
            clipboard ?? new FakeClipboardService(),
            desktopNotifications ?? new FakeDesktopNotificationService());

        return vm;
    }

    private static async Task<MainViewModel> CreateAuthenticatedViewModelAsync(
        FakeBackend backend,
        IFileSavePickerService? fileSavePicker = null,
        IClipboardService? clipboard = null,
        ICardCheckoutWindowService? cardCheckoutWindow = null)
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync("jwt-token", "token", CancellationToken.None);
        await secretStore.SetAsync("refresh-token", "refresh-token", CancellationToken.None);
        await secretStore.SetAsync("account-email", "user@example.com", CancellationToken.None);

        var vm = CreateViewModel(backend, secretStore, fileSavePicker: fileSavePicker, clipboard: clipboard, cardCheckoutWindow: cardCheckoutWindow);
        await vm.InitializeAsync();
        return vm;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for test condition.");
            }

            await Task.Delay(10);
        }
    }

    private static bool CanOpenExclusive(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void InvokeStartLatencyRefresh(MainViewModel vm)
    {
        var method = typeof(MainViewModel).GetMethod("StartLatencyRefresh", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("StartLatencyRefresh method was not found.");

        method.Invoke(vm, null);
    }

    private sealed class FakeBackend : IBackendApiClient
    {
        private readonly bool _tokenValid;
        private readonly bool _throwOnGetServers;
        private readonly bool _subscriptionIsPro;
        private readonly IReadOnlyList<VpnServer> _servers;
        private readonly Exception? _serverException;
        private int _remainingTransientServerFailures;

        public FakeBackend(
            bool tokenValid = false,
            bool throwOnGetServers = false,
            bool subscriptionIsPro = false,
            IReadOnlyList<VpnServer>? servers = null,
            int transientServerFailures = 0,
            Exception? serverException = null)
        {
            _tokenValid = tokenValid;
            _throwOnGetServers = throwOnGetServers;
            _subscriptionIsPro = subscriptionIsPro;
            _servers = servers ?? [];
            _remainingTransientServerFailures = transientServerFailures;
            _serverException = serverException;
            DnsPreferenceResponse = new DnsPreferenceResponse(false, subscriptionIsPro, false, "Standard", 15);
        }

        public TaskCompletionSource? LoginStarted { get; init; }
        public TaskCompletionSource? LoginReleased { get; init; }
        public TaskCompletionSource? UsageQuotaEntered { get; init; }
        public TaskCompletionSource? UsageQuotaRelease { get; init; }
        public Func<string, string, DeviceRegistrationPayload, CancellationToken, Task<LoginResponse>>? LoginHandler { get; set; }
        public Func<RegisterRequest, CancellationToken, Task<RegisterResponse>>? RegisterHandler { get; set; }
        public Func<string, DeviceRegistrationPayload, CancellationToken, Task<LoginResponse>>? RefreshHandler { get; set; }
        public Func<string?, CancellationToken, Task<ApiMessage>>? LogoutHandler { get; set; }
        public Func<CancellationToken, Task<UsageQuota>>? UsageQuotaHandler { get; set; }
        public Func<CancellationToken, Task<SubscriptionStatus>>? SubscriptionStatusHandler { get; set; }
        public Func<CancellationToken, Task<DnsPreferenceResponse>>? DnsPreferenceHandler { get; set; }
        public Func<bool, CancellationToken, Task<DnsPreferenceResponse>>? UpdateDnsPreferenceHandler { get; set; }
        public TwoFactorStatus TwoFactorStatusResponse { get; set; } = new(false, false, 0, null);
        public TwoFactorSetup TwoFactorSetupResponse { get; set; } = new("AAAA BBBB", "otpauth://totp/libreguard", "AAAABBBB", null);
        public Exception? SetupTwoFactorException { get; set; }
        public Exception? DisableTwoFactorException { get; set; }
        public Exception? EnableTwoFactorException { get; set; }
        public IReadOnlyList<UserCertificate> CertificatesResponse { get; init; } = [];
        public int SetupTwoFactorCalls { get; private set; }
        public int EnableTwoFactorCalls { get; private set; }
        public int DisableTwoFactorCalls { get; private set; }
        public int GetServersCalls { get; private set; }
        public int GetUsageQuotaCalls { get; private set; }
        public int GetSubscriptionStatusCalls { get; private set; }
        public int GetDevicesCalls { get; private set; }
        public int GetTwoFactorStatusCalls { get; private set; }
        public int GetCertificatesCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int GetDnsPreferenceCalls { get; private set; }
        public int UpdateDnsPreferenceCalls { get; private set; }
        public bool? LastRequestedAdBlockingEnabled { get; private set; }
        public int RemoveDeviceCalls { get; private set; }
        public int? LastRemovedDeviceId { get; private set; }
        public BackendApiException? LoginException { get; init; }
        public BackendApiException? VerifyTwoFactorException { get; init; }
        public BackendApiException? VerifyRecoveryCodeException { get; init; }
        public int? LastDownloadedCertificateConfigId { get; private set; }
        public int? LastDownloadedCertificateId { get; private set; }
        public string? LastEnabledTwoFactorCode { get; private set; }
        public SubscriptionStatus? SubscriptionStatusResponse { get; init; }
        public DnsPreferenceResponse DnsPreferenceResponse { get; set; }
        public IReadOnlyList<UserDevice> DevicesResponse { get; init; } = [];
        public BackendApiException? GoogleCodeLoginException { get; init; }
        public int PreAuthRemoveCalls { get; private set; }
        public string? LastPreAuthRemoveEmail { get; private set; }
        public string? LastPreAuthRemovePassword { get; private set; }
        public int? LastPreAuthRemoveDeviceId { get; private set; }
        public int PreAuthOAuthRemoveCalls { get; private set; }
        public string? LastPreAuthOAuthProvider { get; private set; }
        public string? LastPreAuthOAuthIdToken { get; private set; }
        public int? LastPreAuthOAuthDeviceId { get; private set; }
        public int PreAuthOAuthCodeRemoveCalls { get; private set; }
        public string? LastPreAuthOAuthCodeProvider { get; private set; }
        public GoogleOAuthAuthorizationCode? LastPreAuthOAuthAuthorizationCode { get; private set; }
        public int? LastPreAuthOAuthCodeDeviceId { get; private set; }
        public int GetMoneroPriceCalls { get; private set; }
        public int CreateMoneroInvoiceCalls { get; private set; }
        public int GetMoneroPaymentStatusCalls { get; private set; }
        public int GetLatestMoneroInvoiceCalls { get; private set; }
        public int CreateCardCheckoutCalls { get; private set; }
        public BillingCycle? LastMoneroPriceCycle { get; private set; }
        public BillingCycle? LastCreatedMoneroInvoiceCycle { get; private set; }
        public BillingCycle? LastCheckoutCycle { get; private set; }
        public string? LastMoneroStatusInvoiceId { get; private set; }
        public MoneroPriceResponse MoneroPriceResponse { get; set; } = new(0.04m, 5.99m, 149.75m, "XMR", "LibreGuard Pro");
        public MoneroInvoiceResponse MoneroInvoiceResponse { get; set; } = new("invoice-1", "xmr-address", 0.04m, "XMR", "Pending", "LibreGuard Pro", DateTimeOffset.UtcNow, "Monthly");
        public MoneroStatusResponse MoneroStatusResponse { get; set; } = new("invoice-1", "Pending", 0.04m, 0m, 0, 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24), "Monthly");
        public CardCheckoutResponse CardCheckoutResponse { get; set; } = new("https://checkout.example/pro", "ch_123", 42, "Monthly", 5.99m, "USD", "prod_monthly", "user@example.com", "card-42");
        public CardPaymentStatusResponse CardPaymentStatusResponse { get; set; } = new("ch_123", "Pending", 5.99m, 0m, null, null, DateTimeOffset.UtcNow);

        public void ResetAccountStateCallCounts()
        {
            GetServersCalls = 0;
            GetUsageQuotaCalls = 0;
            GetSubscriptionStatusCalls = 0;
            GetDevicesCalls = 0;
            GetTwoFactorStatusCalls = 0;
            GetCertificatesCalls = 0;
            RefreshCalls = 0;
            GetDnsPreferenceCalls = 0;
            UpdateDnsPreferenceCalls = 0;
            LastRequestedAdBlockingEnabled = null;
            LastDownloadedCertificateConfigId = null;
            LastDownloadedCertificateId = null;
        }

        public void SetBearerToken(string? token) { }
        public Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
            => RegisterHandler is not null
                ? RegisterHandler(request, cancellationToken)
                : throw new NotSupportedException();
        public Task<ApiMessage> ResendConfirmationAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EmailConfirmationStatus> CheckConfirmationAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<LoginResponse> LoginAsync(string email, string password, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        {
            if (LoginException is not null)
            {
                return await Task.FromException<LoginResponse>(LoginException);
            }

            if (LoginHandler is not null)
            {
                return await LoginHandler(email, password, device, cancellationToken);
            }

            if (LoginStarted is null || LoginReleased is null)
            {
                throw new NotSupportedException();
            }

            LoginStarted.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                return new LoginResponse();
            }
            finally
            {
                LoginReleased.TrySetResult();
            }
        }
        public Task<LoginResponse> VerifyTwoFactorAsync(string email, string code, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
            => VerifyTwoFactorException is null
                ? throw new NotSupportedException()
                : Task.FromException<LoginResponse>(VerifyTwoFactorException);
        public Task<LoginResponse> VerifyRecoveryCodeAsync(string email, string recoveryCode, string pendingLoginToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
            => VerifyRecoveryCodeException is null
                ? throw new NotSupportedException()
                : Task.FromException<LoginResponse>(VerifyRecoveryCodeException);
        public Task<LoginResponse> RefreshAsync(string refreshToken, DeviceRegistrationPayload device, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return RefreshHandler is not null
                ? RefreshHandler(refreshToken, device, cancellationToken)
                : throw new NotSupportedException();
        }
        public Task<ApiMessage> LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
            => LogoutHandler is not null
                ? LogoutHandler(refreshToken, cancellationToken)
                : throw new NotSupportedException();
        public Task<TokenCheckResponse> CheckTokenAsync(CancellationToken cancellationToken) => Task.FromResult(new TokenCheckResponse { IsValid = _tokenValid, Email = "user@example.com" });
        public Task<ApiMessage> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginWithGoogleAsync(string token, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> LoginWithGoogleCodeAsync(GoogleOAuthAuthorizationCode authorizationCode, DeviceRegistrationPayload device, CancellationToken cancellationToken)
            => GoogleCodeLoginException is null
                ? throw new NotSupportedException()
                : Task.FromException<LoginResponse>(GoogleCodeLoginException);
        public Task<ApiMessage> RemovePreAuthDeviceAsync(string email, string password, int deviceId, CancellationToken cancellationToken)
        {
            PreAuthRemoveCalls++;
            LastPreAuthRemoveEmail = email;
            LastPreAuthRemovePassword = password;
            LastPreAuthRemoveDeviceId = deviceId;
            return Task.FromResult(new ApiMessage { Message = "Device removed successfully. You can now retry login." });
        }
        public Task<ApiMessage> RemovePreAuthOAuthDeviceAsync(string provider, string idToken, int deviceId, CancellationToken cancellationToken)
        {
            PreAuthOAuthRemoveCalls++;
            LastPreAuthOAuthProvider = provider;
            LastPreAuthOAuthIdToken = idToken;
            LastPreAuthOAuthDeviceId = deviceId;
            return Task.FromResult(new ApiMessage { Message = "Device removed successfully. You can now retry login." });
        }
        public Task<ApiMessage> RemovePreAuthOAuthDeviceWithCodeAsync(string provider, GoogleOAuthAuthorizationCode authorizationCode, int deviceId, CancellationToken cancellationToken)
        {
            PreAuthOAuthCodeRemoveCalls++;
            LastPreAuthOAuthCodeProvider = provider;
            LastPreAuthOAuthAuthorizationCode = authorizationCode;
            LastPreAuthOAuthCodeDeviceId = deviceId;
            return Task.FromResult(new ApiMessage { Message = "Device removed successfully. You can now retry login." });
        }
        public Task<LoginResponse> ExchangeOAuthTokenAsync(string email, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LoginResponse> CompleteOAuthAsync(string email, string provider, DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TwoFactorStatus> GetTwoFactorStatusAsync(CancellationToken cancellationToken)
        {
            GetTwoFactorStatusCalls++;
            return Task.FromResult(TwoFactorStatusResponse);
        }
        public Task<TwoFactorSetup> SetupTwoFactorAsync(CancellationToken cancellationToken)
        {
            SetupTwoFactorCalls++;
            return SetupTwoFactorException is not null
                ? Task.FromException<TwoFactorSetup>(SetupTwoFactorException)
                : Task.FromResult(TwoFactorSetupResponse);
        }
        public Task<ApiMessage> EnableTwoFactorAsync(string code, CancellationToken cancellationToken)
        {
            EnableTwoFactorCalls++;
            LastEnabledTwoFactorCode = code;
            if (EnableTwoFactorException is not null)
            {
                return Task.FromException<ApiMessage>(EnableTwoFactorException);
            }

            TwoFactorStatusResponse = TwoFactorStatusResponse with { Is2faEnabled = true, HasAuthenticator = true };
            return Task.FromResult(new ApiMessage { Message = "2FA enabled." });
        }
        public Task<ApiMessage> DisableTwoFactorAsync(CancellationToken cancellationToken)
        {
            DisableTwoFactorCalls++;
            if (DisableTwoFactorException is not null)
            {
                return Task.FromException<ApiMessage>(DisableTwoFactorException);
            }

            TwoFactorStatusResponse = TwoFactorStatusResponse with { Is2faEnabled = false };
            return Task.FromResult(new ApiMessage { Message = "2FA disabled." });
        }
        public Task<ApiMessage> ResetTwoFactorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RecoveryCodesResponse> GenerateRecoveryCodesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<VpnServer>> GetServersAsync(CancellationToken cancellationToken)
        {
            GetServersCalls++;
            if (_serverException is not null)
            {
                return Task.FromException<IReadOnlyList<VpnServer>>(_serverException);
            }

            if (_remainingTransientServerFailures-- > 0)
            {
                return Task.FromException<IReadOnlyList<VpnServer>>(new HttpRequestException("temporary server-list failure"));
            }

            return _throwOnGetServers
                ? Task.FromException<IReadOnlyList<VpnServer>>(new InvalidOperationException("server list unavailable"))
                : Task.FromResult(_servers);
        }
        public Task<VpnConfigResponse> GetVpnConfigAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VpnConfigResponse> GetVpnConfigQueryAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadOpenVpnConfigAsync(int serverId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<UserCertificate>> GetCertificatesAsync(CancellationToken cancellationToken)
        {
            GetCertificatesCalls++;
            return Task.FromResult(CertificatesResponse);
        }
        public Task<CertificateRequestResponse> RequestCertificateAsync(int serverId, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CertificateJob> GetCertificateJobAsync(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<UsageQuota> GetUsageQuotaAsync(CancellationToken cancellationToken)
        {
            GetUsageQuotaCalls++;
            if (UsageQuotaHandler is not null)
            {
                return await UsageQuotaHandler(cancellationToken);
            }

            UsageQuotaEntered?.TrySetResult();
            if (UsageQuotaRelease is not null)
            {
                await UsageQuotaRelease.Task.WaitAsync(cancellationToken);
            }

            return new UsageQuota { BytesUsed = 0, BytesLimit = null, IsUnlimited = true };
        }
        public Task<UsageQuota> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(new UsageQuota { Allowed = true, BytesUsed = 0, BytesLimit = null, IsUnlimited = true });
        public Task<SubscriptionStatus> GetSubscriptionStatusAsync(CancellationToken cancellationToken)
        {
            GetSubscriptionStatusCalls++;
            if (SubscriptionStatusHandler is not null)
            {
                return SubscriptionStatusHandler(cancellationToken);
            }

            return Task.FromResult(SubscriptionStatusResponse
                ?? new SubscriptionStatus(_subscriptionIsPro ? "Pro" : "Free", _subscriptionIsPro, "Active", null, "Monthly", 1, 3, true, null));
        }
        public Task<DnsPreferenceResponse> GetDnsPreferenceAsync(CancellationToken cancellationToken)
        {
            GetDnsPreferenceCalls++;
            return DnsPreferenceHandler is not null
                ? DnsPreferenceHandler(cancellationToken)
                : Task.FromResult(DnsPreferenceResponse);
        }
        public async Task<DnsPreferenceResponse> UpdateDnsPreferenceAsync(bool enabled, CancellationToken cancellationToken)
        {
            UpdateDnsPreferenceCalls++;
            LastRequestedAdBlockingEnabled = enabled;
            if (UpdateDnsPreferenceHandler is not null)
            {
                return await UpdateDnsPreferenceHandler(enabled, cancellationToken);
            }

            DnsPreferenceResponse = DnsPreferenceResponse with
            {
                RequestedEnabled = enabled,
                EffectiveEnabled = enabled && DnsPreferenceResponse.CanUseAdBlocking,
                EffectiveMode = enabled && DnsPreferenceResponse.CanUseAdBlocking ? "AdBlocking" : "Standard"
            };
            return DnsPreferenceResponse;
        }
        public Task<ServerAccessResponse> CanAccessServerAsync(int serverTier, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SubscriptionDeviceRegistrationResponse> RegisterSubscriptionDeviceAsync(DeviceRegistrationPayload device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveSubscriptionDeviceAsync(string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CheckoutUrlResponse> GetCheckoutUrlAsync(string cycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MoneroPriceResponse> GetMoneroPriceAsync(BillingCycle cycle, CancellationToken cancellationToken)
        {
            GetMoneroPriceCalls++;
            LastMoneroPriceCycle = cycle;
            return Task.FromResult(MoneroPriceResponse);
        }

        public Task<MoneroInvoiceResponse> CreateMoneroInvoiceAsync(BillingCycle cycle, CancellationToken cancellationToken)
        {
            CreateMoneroInvoiceCalls++;
            LastCreatedMoneroInvoiceCycle = cycle;
            return Task.FromResult(MoneroInvoiceResponse);
        }

        public Task<MoneroStatusResponse> GetMoneroPaymentStatusAsync(string invoiceId, CancellationToken cancellationToken)
        {
            GetMoneroPaymentStatusCalls++;
            LastMoneroStatusInvoiceId = invoiceId;
            return Task.FromResult(MoneroStatusResponse);
        }

        public Task<MoneroInvoiceResponse> GetLatestMoneroInvoiceAsync(CancellationToken cancellationToken)
        {
            GetLatestMoneroInvoiceCalls++;
            return Task.FromResult(MoneroInvoiceResponse with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        }

        public Task<CardCheckoutResponse> CreateCardCheckoutAsync(BillingCycle cycle, CancellationToken cancellationToken)
        {
            CreateCardCheckoutCalls++;
            LastCheckoutCycle = cycle;
            return Task.FromResult(CardCheckoutResponse);
        }

        public Task<CardPaymentStatusResponse> GetCardPaymentStatusAsync(string transactionId, CancellationToken cancellationToken)
            => Task.FromResult(CardPaymentStatusResponse);
        public Task<IReadOnlyList<UserDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            GetDevicesCalls++;
            return Task.FromResult(DevicesResponse);
        }
        public Task<ApiMessage> RemoveDeviceAsync(int id, CancellationToken cancellationToken)
        {
            RemoveDeviceCalls++;
            LastRemovedDeviceId = id;
            return Task.FromResult(new ApiMessage { Message = "Device logged out." });
        }
        public Task<ApiMessage> DeleteDeviceAsync(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveAllOtherDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiMessage> RemoveAllInactiveDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> DownloadCertificateConfigAsync(int certificateId, CancellationToken cancellationToken)
        {
            LastDownloadedCertificateConfigId = certificateId;
            return Task.FromResult<Stream>(new MemoryStream("config"u8.ToArray()));
        }

        public Task<Stream> DownloadCertificateAsync(int certificateId, CancellationToken cancellationToken)
        {
            LastDownloadedCertificateId = certificateId;
            return Task.FromResult<Stream>(new MemoryStream("certificate"u8.ToArray()));
        }
    }

    private sealed class TemporaryXdgStateHome : IDisposable
    {
        private readonly string? _previousXdgStateHome;

        public TemporaryXdgStateHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "libreguard-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            _previousXdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", _previousXdgStateHome);
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FakeDeviceIdentityService : IDeviceIdentityService
    {
        public Task<DeviceRegistrationPayload> GetRegistrationPayloadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DeviceRegistrationPayload("device", "1.0.0", "key", "key-id", "RSA-OAEP-256"));

        public Task<string> DecryptPassphraseAsync(EncryptedPassphrase encryptedPassphrase, CancellationToken cancellationToken)
            => Task.FromResult("pass");
    }

    private sealed class FakeVpnConnectionService : IVpnConnectionService
    {
        public VpnServer? LastConnectedServer { get; private set; }
        public bool HoldConnectOpen { get; init; }
        public TaskCompletionSource? ConnectStarted { get; init; }
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int ShutdownCalls { get; private set; }

        public event EventHandler<VpnStatus>? StatusChanged;
        public Task<VpnStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new VpnStatus(VpnConnectionState.Disconnected, null, null));
        public Task ConnectAsync(VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            LastConnectedServer = server;
            ConnectStarted?.TrySetResult();

            if (!HoldConnectOpen)
            {
                return Task.CompletedTask;
            }

            return WaitForCancellationAsync(cancellationToken);
        }
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            return Task.CompletedTask;
        }
        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCalls++;
            StatusChanged?.Invoke(this, new VpnStatus(VpnConnectionState.Disconnected, null, "Disconnected"));
            return Task.CompletedTask;
        }
        public Task<VpnProfile> ImportOrUpdateProfileAsync(VpnConfigResponse config, VpnServer server, VpnProtocol protocol, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void RaiseStatus(VpnStatus status)
            => StatusChanged?.Invoke(this, status);

        private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        }
    }

    private sealed class FakeTunnelTrafficMonitor : ITunnelTrafficMonitor
    {
        public TunnelTrafficSnapshot StartSnapshot { get; set; } = new(null, 0, 0, 0, 0, false);
        public TunnelTrafficSnapshot RefreshSnapshot { get; set; } = new(null, 0, 0, 0, 0, false);
        public string? LastStartedProfile { get; private set; }
        public bool StopCalled { get; private set; }

        public Task<TunnelTrafficSnapshot> StartSessionAsync(string profileName, CancellationToken cancellationToken)
        {
            LastStartedProfile = profileName;
            StopCalled = false;
            return Task.FromResult(StartSnapshot);
        }

        public Task<TunnelTrafficSnapshot> RefreshAsync(CancellationToken cancellationToken)
            => Task.FromResult(RefreshSnapshot);

        public void Stop()
        {
            StopCalled = true;
        }
    }

    private sealed class FakeLatencyService : IServerLatencyService
    {
        private readonly Dictionary<string, int> _cachedLatencies;
        private readonly Func<IReadOnlyList<VpnServer>, int, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? _measureHandler;

        public FakeLatencyService()
            : this(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
        {
        }

        public FakeLatencyService(Dictionary<string, int> cachedLatencies)
            : this(cachedLatencies, null)
        {
        }

        public FakeLatencyService(Func<IReadOnlyList<VpnServer>, int, CancellationToken, Task<IReadOnlyDictionary<string, int>>> measureHandler)
            : this(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), measureHandler)
        {
        }

        private FakeLatencyService(
            Dictionary<string, int> cachedLatencies,
            Func<IReadOnlyList<VpnServer>, int, CancellationToken, Task<IReadOnlyDictionary<string, int>>>? measureHandler)
        {
            _cachedLatencies = new Dictionary<string, int>(cachedLatencies, StringComparer.OrdinalIgnoreCase);
            _measureHandler = measureHandler;
        }

        public int MeasureCalls { get; private set; }
        public IReadOnlyList<VpnServer> LastMeasuredServers { get; private set; } = [];

        public async Task<IReadOnlyDictionary<string, int>> MeasureLatenciesAsync(IReadOnlyList<VpnServer> servers, CancellationToken cancellationToken)
        {
            MeasureCalls++;
            LastMeasuredServers = servers.ToList();

            IReadOnlyDictionary<string, int> results = _measureHandler is null
                ? new Dictionary<string, int>(_cachedLatencies, StringComparer.OrdinalIgnoreCase)
                : await _measureHandler(servers, MeasureCalls, cancellationToken);

            foreach (var item in results)
            {
                _cachedLatencies[item.Key] = item.Value;
            }

            return new Dictionary<string, int>(results, StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyDictionary<string, int> GetCachedLatencies()
            => new Dictionary<string, int>(_cachedLatencies, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = [];
        public int ExitCode { get; init; }
        public int StartDetachedCalls { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            LastFileName = fileName;
            LastArguments = arguments.ToList();
            return Task.FromResult(new ProcessResult(ExitCode, string.Empty, string.Empty));
        }

        public Task<ProcessResult> StartDetachedAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            StartDetachedCalls++;
            LastFileName = fileName;
            LastArguments = arguments.ToList();
            return Task.FromResult(new ProcessResult(ExitCode, string.Empty, string.Empty));
        }
    }

    private sealed class FakeCardCheckoutWindowService : ICardCheckoutWindowService
    {
        public int ShowCalls { get; private set; }
        public int BrowserOpenCalls { get; private set; }
        public int MonitorCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public bool IsCheckoutActive { get; set; }
        public string? LastBrowserUrl { get; private set; }
        public CardCheckoutWindowRequest? LastRequest { get; private set; }
        public CardCheckoutWindowResult Result { get; set; } = CardCheckoutWindowResult.Closed;
        public CardCheckoutWindowResult MonitorResult { get; set; } = CardCheckoutWindowResult.Closed;
        public ExternalUriLaunchResult BrowserLaunchResult { get; set; } = new(true);
        public TaskCompletionSource<CardCheckoutWindowResult>? ShowCompletion { get; set; }

        public Task<CardCheckoutWindowResult> ShowCheckoutAsync(CardCheckoutWindowRequest request, CancellationToken cancellationToken)
        {
            ShowCalls++;
            LastRequest = request;
            return ShowCompletion?.Task ?? Task.FromResult(Result);
        }

        public Task<CardCheckoutWindowResult> MonitorCheckoutAsync(string transactionId, CancellationToken cancellationToken)
        {
            MonitorCalls++;
            return Task.FromResult(MonitorResult);
        }

        public Task<ExternalUriLaunchResult> OpenInBrowserAsync(string checkoutUrl, CancellationToken cancellationToken)
        {
            BrowserOpenCalls++;
            LastBrowserUrl = checkoutUrl;
            return Task.FromResult(BrowserLaunchResult);
        }

        public void CancelCheckout() => CancelCalls++;
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDesktopNotificationService : IDesktopNotificationService
    {
        public List<(string Title, string Body)> Messages { get; } = [];

        public Task ShowAsync(string title, string body, CancellationToken cancellationToken)
        {
            Messages.Add((title, body));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileSavePickerService : IFileSavePickerService, IDisposable
    {
        private readonly List<Stream> _streams = [];

        public FakeFileSavePickerService()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "libreguard-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }
        public string? LastSuggestedFileName { get; private set; }

        public Task<FileSaveTarget?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken)
        {
            LastSuggestedFileName = suggestedFileName;
            var path = Path.Combine(DirectoryPath, suggestedFileName);
            var stream = File.Create(path);
            _streams.Add(stream);
            return Task.FromResult<FileSaveTarget?>(new FileSaveTarget(stream, path));
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class FakeGoogleOAuthService : IGoogleOAuthService
    {
        public Task<GoogleOAuthAuthorizationCode> AuthenticateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new GoogleOAuthAuthorizationCode(
                "google-client-id.apps.googleusercontent.com",
                "authorization-code",
                "http://127.0.0.1:54321/callback",
                "code-verifier"));
    }

    private sealed class FakePreflightService : ILinuxPreflightService
    {
        public Task<LinuxPreflightResult> CheckAsync(VpnProtocol protocol, CancellationToken cancellationToken)
            => Task.FromResult(new LinuxPreflightResult([new LinuxPreflightCheck("test", true, true, "ok")]));
    }

    private sealed class FakeThemePreferenceService : IThemePreferenceService
    {
        public event EventHandler? PreferenceChanged;

        public AppThemePreference CurrentPreference { get; private set; } = AppThemePreference.System;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetPreferenceAsync(AppThemePreference preference, CancellationToken cancellationToken)
        {
            var changed = CurrentPreference != preference;
            CurrentPreference = preference;
            if (changed)
            {
                PreferenceChanged?.Invoke(this, EventArgs.Empty);
            }

            return Task.CompletedTask;
        }
    }
}
