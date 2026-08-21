using System.Text.RegularExpressions;

namespace Libreguard.Vpn.Linux.Services;

/// <summary>
/// Client-authoritative DNS policy shared by every supported VPN protocol.
/// Resolver-side selection, including ad blocking, remains a server concern.
/// </summary>
internal static class PrivateDnsPolicy
{
    internal const string ResolverAddress = "10.254.0.53";
    internal const string RoutingDomain = "~.";
    internal const string ExclusivePriority = "-2147483648";

    private static readonly string[] OpenVpnPolicyLines =
    [
        "pull-filter ignore \"dhcp-option DNS\"",
        "pull-filter ignore \"dhcp-option DNS6\"",
        "pull-filter ignore \"dns server\"",
        "pull-filter ignore \"block-outside-dns\"",
        $"dhcp-option DNS {ResolverAddress}"
    ];

    internal static string NormalizeOpenVpnConfig(string config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config);

        var lines = config.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);
        var normalized = new List<string>(lines.Length + OpenVpnPolicyLines.Length);
        string? inlineBlock = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (inlineBlock is not null)
            {
                normalized.Add(line);
                if (IsClosingInlineBlock(trimmed, inlineBlock))
                {
                    inlineBlock = null;
                }

                continue;
            }

            var openingBlock = GetOpeningInlineBlockName(trimmed);
            if (openingBlock is not null)
            {
                normalized.Add(line);
                inlineBlock = openingBlock;
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed)
                || trimmed.StartsWith('#')
                || trimmed.StartsWith(';'))
            {
                normalized.Add(line);
                continue;
            }

            if (IsDnsOptionDirective(trimmed)
                || IsModernDnsDirective(trimmed)
                || IsDnsPullFilterDirective(trimmed)
                || IsBlockOutsideDnsDirective(trimmed))
            {
                continue;
            }

            normalized.Add(line);
        }

        while (normalized.Count > 0 && string.IsNullOrWhiteSpace(normalized[^1]))
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        normalized.AddRange(OpenVpnPolicyLines);
        return string.Join('\n', normalized) + "\n";
    }

    private static bool IsDnsOptionDirective(string line)
        => Regex.IsMatch(line, @"^(?:--)?dhcp-option\s+dns6?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsModernDnsDirective(string line)
        => Regex.IsMatch(line, @"^(?:--)?dns\s+server(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsDnsPullFilterDirective(string line)
        => Regex.IsMatch(line, @"^(?:--)?pull-filter(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(line, @"dhcp-option\s+dns6?|dns\s+server|block-outside-dns", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsBlockOutsideDnsDirective(string line)
        => Regex.IsMatch(line, @"^(?:--)?block-outside-dns(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? GetOpeningInlineBlockName(string trimmedLine)
    {
        if (!trimmedLine.StartsWith('<')
            || !trimmedLine.EndsWith('>')
            || trimmedLine.StartsWith("</", StringComparison.Ordinal))
        {
            return null;
        }

        var name = trimmedLine[1..^1].Trim();
        return name.Length == 0 || name.Any(char.IsWhiteSpace) ? null : name;
    }

    private static bool IsClosingInlineBlock(string trimmedLine, string expectedName)
    {
        if (!trimmedLine.StartsWith("</", StringComparison.Ordinal) || !trimmedLine.EndsWith('>'))
        {
            return false;
        }

        var name = trimmedLine[2..^1].Trim();
        return string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase);
    }
}
