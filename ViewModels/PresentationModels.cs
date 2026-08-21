namespace Libreguard.Vpn.Linux.ViewModels;

public sealed record ServerGroupViewModel(string Country, int Count, IReadOnlyList<Models.VpnServer> Servers);

public sealed record ChartBarViewModel(string Label, double Value, string Caption, string Accent)
{
    public double Percentage => Math.Clamp(Value, 0, 100);
    public string ValueText => $"{Value:0.#}";
}

public sealed record TrafficUsageRowViewModel(
    string Label,
    string DownloadText,
    string UploadText,
    double DownloadPercentage,
    double UploadPercentage,
    string TotalText);

public sealed record SessionDurationRowViewModel(
    string Label,
    string DurationText,
    double Percentage);
