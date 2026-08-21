using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public sealed class LocalStatisticsStore(ISettingsStore settingsStore) : ILocalStatisticsStore
{
    private const int RetentionMonths = 13;
    private const string StatisticsKeyPrefix = "statistics:";
    private const string StatisticsPeriodKeyPrefix = "statistics-period:";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string GetUserHash(AuthSession? session)
    {
        var identity = !string.IsNullOrWhiteSpace(session?.UserId)
            ? $"user:{session.UserId.Trim()}"
            : !string.IsNullOrWhiteSpace(session?.Email)
                ? $"email:{session.Email.Trim().ToLowerInvariant()}"
                : string.Empty;

        if (string.IsNullOrWhiteSpace(identity))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<LocalStatisticsProfile> LoadProfileAsync(AuthSession? session, bool closeStaleActiveSession, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await LoadProfileCoreAsync(session, cancellationToken);
            if (closeStaleActiveSession && profile.ActiveSession is not null)
            {
                FinalizeActiveSession(profile, profile.ActiveSession.LastObservedAt, "Interrupted");
                await SaveProfileCoreAsync(profile, cancellationToken);
            }

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalStatisticsProfile> StartSessionAsync(AuthSession? session, LocalVpnSession activeSession, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await LoadProfileCoreAsync(session, cancellationToken);
            if (profile.ActiveSession is not null)
            {
                FinalizeActiveSession(profile, profile.ActiveSession.LastObservedAt, "Interrupted");
            }

            profile.ActiveSession = activeSession;
            profile.LastUpdatedAt = DateTimeOffset.UtcNow;
            await SaveProfileCoreAsync(profile, cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalStatisticsProfile> RecordSnapshotAsync(AuthSession? session, TunnelTrafficSnapshot snapshot, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await LoadProfileCoreAsync(session, cancellationToken);
            if (profile.ActiveSession is not { } activeSession)
            {
                return profile;
            }

            var downloadDelta = snapshot.SessionDownloadBytes >= activeSession.LastObservedDownloadBytes
                ? snapshot.SessionDownloadBytes - activeSession.LastObservedDownloadBytes
                : 0;
            var uploadDelta = snapshot.SessionUploadBytes >= activeSession.LastObservedUploadBytes
                ? snapshot.SessionUploadBytes - activeSession.LastObservedUploadBytes
                : 0;

            AddTraffic(profile, observedAt, downloadDelta, uploadDelta);
            AddConnectedSeconds(profile, activeSession.LastObservedAt, observedAt);

            activeSession.DownloadBytes += downloadDelta;
            activeSession.UploadBytes += uploadDelta;
            activeSession.LastObservedDownloadBytes = Math.Max(snapshot.SessionDownloadBytes, activeSession.LastObservedDownloadBytes);
            activeSession.LastObservedUploadBytes = Math.Max(snapshot.SessionUploadBytes, activeSession.LastObservedUploadBytes);
            activeSession.LastObservedAt = observedAt;
            profile.LastUpdatedAt = DateTimeOffset.UtcNow;

            await SaveProfileCoreAsync(profile, cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalStatisticsProfile> FinalizeActiveSessionAsync(AuthSession? session, DateTimeOffset endedAt, string finalStatus, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await LoadProfileCoreAsync(session, cancellationToken);
            if (profile.ActiveSession is not null)
            {
                FinalizeActiveSession(profile, endedAt, finalStatus);
                await SaveProfileCoreAsync(profile, cancellationToken);
            }

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetStatisticsPeriodAsync(AuthSession? session, CancellationToken cancellationToken)
    {
        var userHash = GetUserHash(session);
        return string.IsNullOrWhiteSpace(userHash)
            ? null
            : await settingsStore.GetAsync<string>(StatisticsPeriodKeyPrefix + userHash, cancellationToken);
    }

    public async Task SetStatisticsPeriodAsync(AuthSession? session, string period, CancellationToken cancellationToken)
    {
        var userHash = GetUserHash(session);
        if (string.IsNullOrWhiteSpace(userHash))
        {
            return;
        }

        await settingsStore.SetAsync(StatisticsPeriodKeyPrefix + userHash, period, cancellationToken);
    }

    private async Task<LocalStatisticsProfile> LoadProfileCoreAsync(AuthSession? session, CancellationToken cancellationToken)
    {
        var userHash = GetUserHash(session);
        if (string.IsNullOrWhiteSpace(userHash))
        {
            return new LocalStatisticsProfile();
        }

        var profile = await settingsStore.GetAsync<LocalStatisticsProfile>(StatisticsKeyPrefix + userHash, cancellationToken)
            ?? new LocalStatisticsProfile { UserHash = userHash };

        Prune(profile, DateTimeOffset.UtcNow);
        return profile;
    }

    private async Task SaveProfileCoreAsync(LocalStatisticsProfile profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.UserHash))
        {
            return;
        }

        Prune(profile, DateTimeOffset.UtcNow);
        profile.LastUpdatedAt = DateTimeOffset.UtcNow;
        await settingsStore.SetAsync(StatisticsKeyPrefix + profile.UserHash, profile, cancellationToken);
    }

    private static void FinalizeActiveSession(LocalStatisticsProfile profile, DateTimeOffset endedAt, string finalStatus)
    {
        var activeSession = profile.ActiveSession;
        if (activeSession is null)
        {
            return;
        }

        var effectiveEnd = endedAt < activeSession.LastObservedAt ? activeSession.LastObservedAt : endedAt;
        AddConnectedSeconds(profile, activeSession.LastObservedAt, effectiveEnd);
        activeSession.EndedAt = effectiveEnd;
        activeSession.FinalStatus = finalStatus;
        activeSession.LastObservedAt = effectiveEnd;
        profile.CompletedSessions.Add(activeSession);
        profile.ActiveSession = null;
    }

    private static void AddTraffic(LocalStatisticsProfile profile, DateTimeOffset observedAt, long downloadBytes, long uploadBytes)
    {
        if (downloadBytes <= 0 && uploadBytes <= 0)
        {
            return;
        }

        var bucket = GetOrCreateDailyBucket(profile, observedAt.UtcDateTime.Date);
        bucket.DownloadBytes += Math.Max(0, downloadBytes);
        bucket.UploadBytes += Math.Max(0, uploadBytes);
    }

    private static void AddConnectedSeconds(LocalStatisticsProfile profile, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        if (endedAt <= startedAt)
        {
            return;
        }

        var cursor = startedAt.UtcDateTime;
        var end = endedAt.UtcDateTime;
        while (cursor < end)
        {
            var nextDay = cursor.Date.AddDays(1);
            var segmentEnd = end < nextDay ? end : nextDay;
            var seconds = (long)Math.Round((segmentEnd - cursor).TotalSeconds);
            if (seconds > 0)
            {
                GetOrCreateDailyBucket(profile, cursor.Date).ConnectedSeconds += seconds;
            }

            cursor = segmentEnd;
        }
    }

    private static LocalDailyTraffic GetOrCreateDailyBucket(LocalStatisticsProfile profile, DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var bucket = profile.DailyTraffic.FirstOrDefault(item => string.Equals(item.Date, key, StringComparison.Ordinal));
        if (bucket is not null)
        {
            return bucket;
        }

        bucket = new LocalDailyTraffic { Date = key };
        profile.DailyTraffic.Add(bucket);
        return bucket;
    }

    private static void Prune(LocalStatisticsProfile profile, DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime.Date.AddMonths(-RetentionMonths);
        profile.CompletedSessions.RemoveAll(session => (session.EndedAt ?? session.StartedAt).UtcDateTime.Date < cutoff);
        profile.DailyTraffic.RemoveAll(bucket => TryParseDate(bucket.Date, out var date) && date < cutoff);
    }

    private static bool TryParseDate(string value, out DateTime date)
        => DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out date);
}
