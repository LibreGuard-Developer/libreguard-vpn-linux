using System.Text.Json.Serialization;

namespace Libreguard.Vpn.Linux.Models;

public sealed class LocalStatisticsProfile
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("userHash")]
    public string UserHash { get; init; } = string.Empty;

    [JsonPropertyName("activeSession")]
    public LocalVpnSession? ActiveSession { get; set; }

    [JsonPropertyName("completedSessions")]
    public List<LocalVpnSession> CompletedSessions { get; init; } = [];

    [JsonPropertyName("dailyTraffic")]
    public List<LocalDailyTraffic> DailyTraffic { get; init; } = [];

    [JsonPropertyName("lastUpdatedAt")]
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LocalVpnSession
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("endedAt")]
    public DateTimeOffset? EndedAt { get; set; }

    [JsonPropertyName("serverId")]
    public int? ServerId { get; init; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("profileName")]
    public string? ProfileName { get; init; }

    [JsonPropertyName("downloadBytes")]
    public long DownloadBytes { get; set; }

    [JsonPropertyName("uploadBytes")]
    public long UploadBytes { get; set; }

    [JsonPropertyName("lastObservedDownloadBytes")]
    public long LastObservedDownloadBytes { get; set; }

    [JsonPropertyName("lastObservedUploadBytes")]
    public long LastObservedUploadBytes { get; set; }

    [JsonPropertyName("lastObservedAt")]
    public DateTimeOffset LastObservedAt { get; set; }

    [JsonPropertyName("finalStatus")]
    public string? FinalStatus { get; set; }
}

public sealed class LocalDailyTraffic
{
    [JsonPropertyName("date")]
    public string Date { get; init; } = string.Empty;

    [JsonPropertyName("downloadBytes")]
    public long DownloadBytes { get; set; }

    [JsonPropertyName("uploadBytes")]
    public long UploadBytes { get; set; }

    [JsonPropertyName("connectedSeconds")]
    public long ConnectedSeconds { get; set; }
}
