using System.Net;
using System.Net.Http;
using MindMap.Services;

namespace MindMap.Tests;

public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("V2.0", "2.0.0")]
    public void ParseTagNormalizesReleaseTags(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateCheck.ParseTag(tag));
    }

    [Fact]
    public void IsNewerIgnoresBuildRevisionAndComparesSemanticVersion()
    {
        Assert.False(UpdateCheck.IsNewer(new Version(1, 2, 3, 4), "v1.2.3"));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 2, 3, 4), "v1.2.4"));
    }

    [Fact]
    public void DecideHidesDismissedRelease()
    {
        var release = new ReleaseInfo("1.2.3", DateTimeOffset.UtcNow, "https://github.com/loxsmoke/mindmap/releases/download/v1.2.3/MindMap-1.2.3-setup.exe", 10);

        Assert.Equal(UpdateCheck.BannerState.Shown, UpdateCheck.Decide(release, dismissed: null));
        Assert.Equal(UpdateCheck.BannerState.Hidden, UpdateCheck.Decide(release, dismissed: "1.2.3"));
    }

    [Fact]
    public void SetupAssetNameUsesReleaseInstallerName()
    {
        Assert.Equal("MindMap-1.2.3-setup.exe", UpdateCheck.SetupAssetName("1.2.3"));
    }

    [Fact]
    public async Task UpdateServiceReturnsNewerGitHubRelease()
    {
        using var http = new HttpClient(new JsonHandler("""
            {
              "tag_name": "v0.2.0",
              "published_at": "2026-08-18T12:00:00Z",
              "assets": [
                {
                  "name": "MindMap-0.2.0-setup.exe",
                  "browser_download_url": "https://github.com/loxsmoke/mindmap/releases/download/v0.2.0/MindMap-0.2.0-setup.exe",
                  "size": 12345
                }
              ]
            }
            """));

        var release = await UpdateService.CheckAsync(new Version(0, 1, 0), http);

        Assert.NotNull(release);
        Assert.Equal("0.2.0", release.Version);
        Assert.Equal(DateTimeOffset.Parse("2026-08-18T12:00:00Z"), release.PublishedAt);
        Assert.Equal("https://github.com/loxsmoke/mindmap/releases/download/v0.2.0/MindMap-0.2.0-setup.exe", release.SetupUrl);
        Assert.Equal(12345, release.SetupSize);
    }

    [Theory]
    [InlineData("https://github.com/loxsmoke/mindmap/releases/download/v1.2.3/MindMap-1.2.3-setup.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/file.exe", true)]
    [InlineData("http://github.com/loxsmoke/mindmap/releases/download/v1.2.3/MindMap-1.2.3-setup.exe", false)]
    [InlineData("https://example.com/MindMap-1.2.3-setup.exe", false)]
    public void UpdateInstallerAllowsOnlyTrustedHttpsReleaseUrls(string url, bool expected)
    {
        Assert.Equal(expected, UpdateInstaller.IsTrustedUrl(url));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
            return Task.FromResult(response);
        }
    }
}
