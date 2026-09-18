using System.Security.Cryptography;

namespace LocalProjectBridge.Core;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? SetupUrl = null, string? SetupHashUrl = null)
{
    public bool CanInstall => SetupUrl is not null && SetupHashUrl is not null;
}

/// <summary>查询 GitHub 最新正式版本，并下载经 SHA256 校验的安装程序。</summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/keros68/ProjectBridge/releases/latest";
    public const string ReleasesPage = "https://github.com/keros68/ProjectBridge/releases/latest";
    public const string SetupAssetName = "ProjectBridge-Setup.exe";
    private const string DownloadBase = "https://github.com/keros68/ProjectBridge/releases/download/";
    private readonly HttpClient _http;
    private readonly HttpClient _noRedirectHttp;

    public UpdateChecker(HttpClient? http = null, HttpClient? noRedirectHttp = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _noRedirectHttp = noRedirectHttp ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>先查 GitHub API；API 不可达或限流时，改读 releases/latest 页面的跳转地址。两者都失败才抛出异常。</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("ProjectBridge-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            return FindNewer(await response.Content.ReadAsStringAsync(timeout.Token), current);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ReleasesPage);
            request.Headers.UserAgent.ParseAdd("ProjectBridge-UpdateCheck");
            using var response = await _noRedirectHttp.SendAsync(request, cancellationToken);
            var location = response.Headers.Location
                ?? throw new HttpRequestException($"无法获取最新版本（{(int)response.StatusCode}）。", error);
            return FromLatestRedirect(location.ToString(), current);
        }
    }

    /// <summary>由 releases/latest 跳转到的 .../releases/tag/vX.Y.Z 推出版本和安装程序地址（发布约定的文件名）。</summary>
    public static UpdateInfo? FromLatestRedirect(string location, Version current)
    {
        const string marker = "/releases/tag/";
        var index = location.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidDataException("最新版本地址格式无效。");
        var tag = Uri.UnescapeDataString(location[(index + marker.Length)..].Split('?', '#')[0].TrimEnd('/'));
        if (!TryParseTag(tag, out var latest) || latest <= Normalize(current)) return null;
        var page = location.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? location : "https://github.com" + location;
        var setup = DownloadBase + Uri.EscapeDataString(tag) + "/" + SetupAssetName;
        return new UpdateInfo(latest, tag, page, setup, setup + ".sha256");
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
        string? setup = null, hash = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var download = asset.GetProperty("browser_download_url").GetString();
                if (name == SetupAssetName) setup = download;
                else if (name == SetupAssetName + ".sha256") hash = download;
            }
        return new UpdateInfo(latest, tag, string.IsNullOrWhiteSpace(page) ? ReleasesPage : page, setup, hash);
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

    /// <summary>下载安装程序到 <paramref name="directory"/>，校验不通过时删除文件并抛出异常。</summary>
    public async Task<string> DownloadInstallerAsync(UpdateInfo update, string directory, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!update.CanInstall) throw new InvalidOperationException("此版本没有提供安装程序。");
        var expected = ParseSha256(await GetStringAsync(update.SetupHashUrl!, cancellationToken));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"ProjectBridge-Setup-{update.Version.ToString(3)}.exe");
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, update.SetupUrl))
            {
                request.Headers.UserAgent.ParseAdd("ProjectBridge-Update");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(target);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total > 0) progress?.Report((int)(received * 100 / total.Value));
                }
            }
            await VerifySha256Async(target, expected, cancellationToken);
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>解析 "hash  文件名" 或纯 hash 格式的校验文件。</summary>
    public static string ParseSha256(string content)
    {
        var hash = content.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("安装程序校验文件格式无效。");
        return hash.ToLowerInvariant();
    }

    public static async Task VerifySha256Async(string path, string expected, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载的安装程序校验失败，已丢弃。");
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ProjectBridge-Update");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
