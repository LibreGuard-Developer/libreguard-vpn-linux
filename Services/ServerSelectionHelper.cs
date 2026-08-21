using Libreguard.Vpn.Linux.Models;

namespace Libreguard.Vpn.Linux.Services;

public static class ServerSelectionHelper
{
    public static VpnServer? SelectBestServer(IEnumerable<VpnServer> servers, IReadOnlyDictionary<string, int> latencies, bool isPro)
    {
        var eligibleServers = servers.Where(server => isPro || !server.IsPremium).ToList();

        VpnServer? bestServer = null;
        var highestScore = double.NegativeInfinity;

        foreach (var server in eligibleServers)
        {
            if (string.IsNullOrWhiteSpace(server.ServerHostname) ||
                !latencies.TryGetValue(server.ServerHostname, out var latency) ||
                latency <= 0)
            {
                continue;
            }

            var latencyWeight = 0.70;
            var loadWeight = 0.25;

            if (server.LoadPercent >= 70)
            {
                latencyWeight = 0.50;
                loadWeight = 0.50;
            }

            var latencyScore = CalculateLatencyScore(latency);
            var loadScore = CalculateLoadScore(server.LoadPercent);
            var totalScore = (latencyScore * latencyWeight) + (loadScore * loadWeight);

            if (isPro && server.IsPremium)
            {
                totalScore += 10.0 * 0.10;
            }

            if (totalScore > highestScore)
            {
                highestScore = totalScore;
                bestServer = server;
            }
        }

        return bestServer;
    }

    private static double CalculateLatencyScore(int latency)
    {
        if (latency <= 50)
        {
            return 100;
        }

        if (latency <= 150)
        {
            return 100 - ((latency - 50) * 30.0 / 100.0);
        }

        if (latency <= 300)
        {
            return 70 - ((latency - 150) * 30.0 / 150.0);
        }

        if (latency <= 500)
        {
            return 40 - ((latency - 300) * 40.0 / 200.0);
        }

        return 0;
    }

    private static double CalculateLoadScore(int load)
    {
        if (load <= 30)
        {
            return 100;
        }

        if (load <= 60)
        {
            return 100 - ((load - 30) * 30.0 / 30.0);
        }

        if (load <= 80)
        {
            return 70 - ((load - 60) * 40.0 / 20.0);
        }

        if (load <= 90)
        {
            return 30 - ((load - 80) * 20.0 / 10.0);
        }

        return 10 - ((load - 90) * 10.0 / 10.0);
    }
}
