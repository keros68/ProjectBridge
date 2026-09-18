namespace LocalProjectBridge.Core;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl);

/// <summary>查询 GitHub 最新正式版本；只提示，不下载或替换文件。</summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/keros68/ProjectBridge/releases/latest";
    public const string ReleasesPage = "https://github.com/keros68/ProjectBridge/releases/latest";
    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("ProjectBridge-UpdateCheck");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return FindNewer(await response.Content.ReadAsStringAsync(cancellationToken), current);
    }

    public static UpdateInfo? FindNewer(string releaseJson, Version current)
    {
        using var document = JsonDocument.Parse(releaseJson);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseTag(tag, out var latest) || latest <= Normalize(current)) return null;
        var page = root.TryGetProperty("html_url", out var url) ? url.GetString() : null;
        return new UpdateInfo(latest, tag, string.IsNullOrWhiteSpace(page) ? ReleasesPage : page);
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0) text = text[..suffix];
        if (Version.TryParse(text, out var parsed)) { version = Normalize(parsed); return true; }
        version = new Version(0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
