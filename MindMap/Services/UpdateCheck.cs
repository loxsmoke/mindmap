using System;

namespace MindMap.Services;

public static class UpdateCheck
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public enum BannerState
    {
        Unchanged,
        Hidden,
        Shown,
    }

    public static BannerState Decide(ReleaseInfo? latest, string? dismissed)
    {
        if (latest is null) return BannerState.Unchanged;
        if (latest.Version == dismissed) return BannerState.Hidden;
        return BannerState.Shown;
    }

    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];

        return Version.TryParse(s, out var version) ? Normalize(version) : null;
    }

    public static bool IsNewer(Version current, string? latestTag)
    {
        var latest = ParseTag(latestTag);
        return latest is not null && latest > Normalize(current);
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build);
}

public sealed record ReleaseInfo(string Version, DateTimeOffset PublishedAt);
