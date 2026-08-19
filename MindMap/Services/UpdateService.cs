using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace MindMap.Services;

public static class UpdateService
{
    private static readonly HttpClient Default = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<ReleaseInfo?> CheckAsync(
        Version current,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Brand.LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd($"{Brand.AppName}-update-check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await (http ?? Default).SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (!UpdateCheck.IsNewer(current, tag)) return null;
            var version = UpdateCheck.ParseTag(tag)!.ToString();

            var publishedAt = root.TryGetProperty("published_at", out var pubEl)
                              && pubEl.ValueKind == JsonValueKind.String
                              && DateTimeOffset.TryParse(
                                  pubEl.GetString(),
                                  CultureInfo.InvariantCulture,
                                  DateTimeStyles.RoundtripKind,
                                  out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            var (setupUrl, setupSize) = FindSetupAsset(root, version);
            return new ReleaseInfo(version, publishedAt, setupUrl, setupSize);
        }
        catch
        {
            return null;
        }
    }

    private static (string? Url, long Size) FindSetupAsset(JsonElement root, string version)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, 0);

        var wanted = UpdateCheck.SetupAssetName(version);
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl)) continue;
            if (!string.Equals(nameEl.GetString(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlEl)) continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url)) continue;
            var size = asset.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;
            return (url, size);
        }

        return (null, 0);
    }
}
