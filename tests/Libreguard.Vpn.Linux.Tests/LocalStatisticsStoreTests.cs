using Libreguard.Vpn.Linux.Models;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class LocalStatisticsStoreTests
{
    [Fact]
    public async Task Store_SeparatesProfilesAndPeriodsByUserHash()
    {
        var settingsStore = new InMemorySettingsStore();
        var store = new LocalStatisticsStore(settingsStore);
        var userOne = CreateSession("user-1", "one@example.com");
        var userTwo = CreateSession("user-2", "two@example.com");
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        await store.StartSessionAsync(userOne, CreateLocalSession(startedAt, "Berlin"), CancellationToken.None);
        await store.RecordSnapshotAsync(userOne, new TunnelTrafficSnapshot("lgvpn0", 0, 0, 1024, 2048, true), startedAt.AddMinutes(1), CancellationToken.None);
        await store.FinalizeActiveSessionAsync(userOne, startedAt.AddMinutes(2), "Disconnected", CancellationToken.None);
        await store.SetStatisticsPeriodAsync(userOne, "Month", CancellationToken.None);
        await store.SetStatisticsPeriodAsync(userTwo, "Year", CancellationToken.None);

        var profileOne = await store.LoadProfileAsync(userOne, closeStaleActiveSession: false, CancellationToken.None);
        var profileTwo = await store.LoadProfileAsync(userTwo, closeStaleActiveSession: false, CancellationToken.None);

        Assert.NotEqual(store.GetUserHash(userOne), store.GetUserHash(userTwo));
        Assert.Single(profileOne.CompletedSessions);
        Assert.Empty(profileTwo.CompletedSessions);
        Assert.Equal("Month", await store.GetStatisticsPeriodAsync(userOne, CancellationToken.None));
        Assert.Equal("Year", await store.GetStatisticsPeriodAsync(userTwo, CancellationToken.None));
    }

    [Fact]
    public async Task Store_PrunesRecordsOlderThanThirteenMonths()
    {
        var store = new LocalStatisticsStore(new InMemorySettingsStore());
        var session = CreateSession("user-1", "user@example.com");
        var oldStart = DateTimeOffset.UtcNow.AddMonths(-14);
        var recentStart = DateTimeOffset.UtcNow.AddDays(-1);

        await store.StartSessionAsync(session, CreateLocalSession(oldStart, "Old"), CancellationToken.None);
        await store.RecordSnapshotAsync(session, new TunnelTrafficSnapshot("lgvpn0", 0, 0, 1024, 0, true), oldStart.AddMinutes(1), CancellationToken.None);
        await store.FinalizeActiveSessionAsync(session, oldStart.AddMinutes(2), "Disconnected", CancellationToken.None);
        await store.StartSessionAsync(session, CreateLocalSession(recentStart, "Recent"), CancellationToken.None);
        await store.RecordSnapshotAsync(session, new TunnelTrafficSnapshot("lgvpn0", 0, 0, 2048, 0, true), recentStart.AddMinutes(1), CancellationToken.None);
        await store.FinalizeActiveSessionAsync(session, recentStart.AddMinutes(2), "Disconnected", CancellationToken.None);

        var profile = await store.LoadProfileAsync(session, closeStaleActiveSession: false, CancellationToken.None);

        var completed = Assert.Single(profile.CompletedSessions);
        Assert.Equal("Recent", completed.City);
        Assert.DoesNotContain(profile.DailyTraffic, bucket => bucket.DownloadBytes == 1024);
    }

    [Fact]
    public async Task LoadProfileAsync_ClosesStaleActiveSessionAtLastObservedTimestamp()
    {
        var store = new LocalStatisticsStore(new InMemorySettingsStore());
        var session = CreateSession("user-1", "user@example.com");
        var startedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var observedAt = startedAt.AddMinutes(30);

        await store.StartSessionAsync(session, CreateLocalSession(startedAt, "Berlin"), CancellationToken.None);
        await store.RecordSnapshotAsync(session, new TunnelTrafficSnapshot("lgvpn0", 0, 0, 4096, 1024, true), observedAt, CancellationToken.None);

        var profile = await store.LoadProfileAsync(session, closeStaleActiveSession: true, CancellationToken.None);

        Assert.Null(profile.ActiveSession);
        var completed = Assert.Single(profile.CompletedSessions);
        Assert.Equal(observedAt, completed.EndedAt);
        Assert.Equal("Interrupted", completed.FinalStatus);
        Assert.Equal(4096, completed.DownloadBytes);
        Assert.Equal(1024, completed.UploadBytes);
    }

    [Fact]
    public async Task Store_RoundTripsPersistedProfile()
    {
        var settingsStore = new InMemorySettingsStore();
        var writer = new LocalStatisticsStore(settingsStore);
        var reader = new LocalStatisticsStore(settingsStore);
        var session = CreateSession("user-1", "user@example.com");
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await writer.StartSessionAsync(session, CreateLocalSession(startedAt, "Paris"), CancellationToken.None);
        await writer.RecordSnapshotAsync(session, new TunnelTrafficSnapshot("lgvpn0", 0, 0, 512, 256, true), startedAt.AddMinutes(1), CancellationToken.None);
        await writer.FinalizeActiveSessionAsync(session, startedAt.AddMinutes(2), "Disconnected", CancellationToken.None);

        var profile = await reader.LoadProfileAsync(session, closeStaleActiveSession: false, CancellationToken.None);

        var completed = Assert.Single(profile.CompletedSessions);
        Assert.Equal("Paris", completed.City);
        Assert.Equal(512, completed.DownloadBytes);
        Assert.Equal(256, completed.UploadBytes);
        Assert.Contains(profile.DailyTraffic, bucket => bucket.DownloadBytes == 512 && bucket.UploadBytes == 256);
    }

    private static AuthSession CreateSession(string userId, string email)
        => new("token", "refresh", email, userId, "device", 1, 3, "Free");

    private static LocalVpnSession CreateLocalSession(DateTimeOffset startedAt, string city)
        => new()
        {
            StartedAt = startedAt,
            LastObservedAt = startedAt,
            ServerId = 1,
            ServerName = city,
            Country = "Germany",
            City = city,
            Protocol = "IKEv2",
            ProfileName = "profile"
        };
}
