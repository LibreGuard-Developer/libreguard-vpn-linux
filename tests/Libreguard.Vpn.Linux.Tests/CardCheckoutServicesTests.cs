using System.Net;
using System.Text;
using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class CardCheckoutServicesTests
{
    [Fact]
    public async Task MonitorCheckout_PendingThenPaid_ReturnsPaid()
    {
        var service = CreateService(["Pending", "Paid"]);

        var result = await service.MonitorCheckoutAsync("ch_123", CancellationToken.None);

        Assert.Equal(CardCheckoutWindowResult.Paid, result);
    }

    [Fact]
    public async Task MonitorCheckout_TransientFailureThenPaid_ContinuesPolling()
    {
        var handler = new SequenceHandler([
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("temporarily unavailable") },
            JsonStatus("Paid")
        ]);
        var service = CreateService(handler);

        var result = await service.MonitorCheckoutAsync("ch_123", CancellationToken.None);

        Assert.Equal(CardCheckoutWindowResult.Paid, result);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("Failed", CardCheckoutWindowResult.Failed)]
    [InlineData("Canceled", CardCheckoutWindowResult.Canceled)]
    [InlineData("Refunded", CardCheckoutWindowResult.Refunded)]
    public async Task MonitorCheckout_TerminalFailure_ReturnsMappedResult(string status, CardCheckoutWindowResult expected)
    {
        var service = CreateService([status]);

        var result = await service.MonitorCheckoutAsync("ch_123", CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task MonitorCheckout_PendingUntilDeadline_ReturnsTimedOut()
    {
        var clock = new ManualTimeProvider();
        var handler = new SequenceHandler([], fallback: JsonStatus("Pending"));
        var service = CreateService(handler, clock, (delay, _) =>
        {
            clock.Advance(delay);
            return Task.CompletedTask;
        }, pollingTimeout: TimeSpan.FromSeconds(5));

        var result = await service.MonitorCheckoutAsync("ch_123", CancellationToken.None);

        Assert.Equal(CardCheckoutWindowResult.TimedOut, result);
    }

    [Fact]
    public async Task MonitorCheckout_WhenCanceled_StopsImmediately()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(["Pending"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.MonitorCheckoutAsync("ch_123", cancellation.Token));
    }

    [Fact]
    public async Task XdgLauncher_StartsOpenerDetached()
    {
        var runner = new FakeProcessRunner(new ProcessResult(0, string.Empty, string.Empty));
        var launcher = new XdgExternalUriLauncher(runner);

        var result = await launcher.OpenAsync(new Uri("https://checkout.example/session"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("xdg-open", runner.DetachedFileName);
        Assert.Equal("https://checkout.example/session", Assert.Single(runner.Arguments));
    }

    [Fact]
    public async Task XdgLauncher_ReportsOpenerExitError()
    {
        var runner = new FakeProcessRunner(new ProcessResult(3, string.Empty, "no method available"));
        var launcher = new XdgExternalUriLauncher(runner);

        var result = await launcher.OpenAsync(new Uri("https://checkout.example/session"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("The desktop URL opener failed.", result.ErrorMessage);
    }

    private static AvaloniaCardCheckoutWindowService CreateService(IReadOnlyList<string> statuses)
        => CreateService(new SequenceHandler(statuses.Select(JsonStatus).ToArray()));

    private static AvaloniaCardCheckoutWindowService CreateService(
        SequenceHandler handler,
        ManualTimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollingTimeout = null)
    {
        clock ??= new ManualTimeProvider();
        delay ??= (duration, _) =>
        {
            clock.Advance(duration);
            return Task.CompletedTask;
        };
        var backend = new BackendApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://management.libreguard.test") });
        return new AvaloniaCardCheckoutWindowService(
            backend,
            new PassThroughAuthSessionService(),
            new StubExternalUriLauncher(),
            clock,
            delay,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            pollingTimeout ?? TimeSpan.FromMinutes(15));
    }

    private static HttpResponseMessage JsonStatus(string status)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"transactionId":"ch_123","status":"{{status}}","amountRequired":5.99,"amountReceived":0,"confirmedAt":null,"expiresAt":null,"serverTime":"2026-07-10T12:00:00Z"}""",
                Encoding.UTF8,
                "application/json")
        };

    private sealed class SequenceHandler(IEnumerable<HttpResponseMessage> responses, HttpResponseMessage? fallback = null) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = _responses.Count > 0 ? _responses.Dequeue() : Clone(fallback ?? JsonStatus("Pending"));
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Clone(HttpResponseMessage source)
            => new(source.StatusCode)
            {
                Content = source.Content is null
                    ? null
                    : new StringContent(source.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, "application/json")
            };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-07-10T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeProcessRunner(ProcessResult result) : IProcessRunner
    {
        public string? FileName { get; private set; }
        public string? DetachedFileName { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            FileName = fileName;
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }

        public Task<ProcessResult> StartDetachedAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
        {
            DetachedFileName = fileName;
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class StubExternalUriLauncher : IExternalUriLauncher
    {
        public Task<ExternalUriLaunchResult> OpenAsync(Uri uri, CancellationToken cancellationToken)
            => Task.FromResult(new ExternalUriLaunchResult(true));
    }

    private sealed class PassThroughAuthSessionService : IAuthSessionService
    {
        public AuthSession? CurrentSession => null;
        public Task SetSessionAsync(AuthSession session, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TryRestoreSessionAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TryRefreshSessionAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task ClearSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> GetRefreshTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<T> ExecuteAuthorizedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
            => operation(cancellationToken);
        public Task ExecuteAuthorizedAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
            => operation(cancellationToken);
    }
}
