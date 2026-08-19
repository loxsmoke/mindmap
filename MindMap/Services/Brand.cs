namespace MindMap.Services;

public static class Brand
{
    public const string AppName = "MindMap";
    public const string RepoUrl = "https://github.com/loxsmoke/mindmap";
    public const string ReleasesUrl = RepoUrl + "/releases/latest";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/loxsmoke/mindmap/releases/latest";

    public static string ReleaseTagUrl(string version) => $"{RepoUrl}/releases/tag/v{version}";
}
