using LocalProjectBridge.Core;

namespace LocalProjectBridge.Core.Tests;

public sealed class UpdateCheckerTests
{
    private static string Release(string tag, bool prerelease = false)
        => $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/keros68/ProjectBridge/releases/tag/{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}}}""";

    [Fact]
    public void ReportsNewerRelease()
    {
        var update = UpdateChecker.FindNewer(Release("v0.1.2"), new Version(0, 1, 1, 0));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 1, 2), update.Version);
        Assert.EndsWith("/v0.1.2", update.PageUrl);
    }

    [Theory]
    [InlineData("v0.1.1")]
    [InlineData("v0.1.0")]
    [InlineData("not-a-version")]
    public void IgnoresSameOlderOrUnparsedRelease(string tag)
        => Assert.Null(UpdateChecker.FindNewer(Release(tag), new Version(0, 1, 1, 0)));

    [Fact]
    public void IgnoresPrerelease()
        => Assert.Null(UpdateChecker.FindNewer(Release("v0.2.0", prerelease: true), new Version(0, 1, 1)));

    [Fact]
    public void ParsesTagWithSuffix()
    {
        Assert.True(UpdateChecker.TryParseTag("V1.2.3-beta+abc", out var version));
        Assert.Equal(new Version(1, 2, 3), version);
    }

    [Fact]
    public void FindsInstallerAssets()
    {
        const string json = """
            {"tag_name":"v0.2.0","html_url":"https://example/r","draft":false,"prerelease":false,"assets":[
              {"name":"ProjectBridge.zip","browser_download_url":"https://example/zip"},
              {"name":"ProjectBridge-Setup.exe","browser_download_url":"https://example/setup"},
              {"name":"ProjectBridge-Setup.exe.sha256","browser_download_url":"https://example/setup.sha256"}]}
            """;
        var update = UpdateChecker.FindNewer(json, new Version(0, 1, 4));
        Assert.NotNull(update);
        Assert.True(update.CanInstall);
        Assert.Equal("https://example/setup", update.SetupUrl);
        Assert.Equal("https://example/setup.sha256", update.SetupHashUrl);
        Assert.False(UpdateChecker.FindNewer(Release("v0.2.0"), new Version(0, 1, 4))!.CanInstall);
    }

    [Fact]
    public void ParsesSha256File()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateChecker.ParseSha256($"{hash.ToUpperInvariant()}  ProjectBridge-Setup.exe"));
        Assert.Throws<InvalidDataException>(() => UpdateChecker.ParseSha256("not-a-hash  file"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadKeepsOnlyVerifiedInstaller(bool hashMatches)
    {
        var payload = "fake installer"u8.ToArray();
        var hash = hashMatches
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload))
            : new string('0', 64);
        var http = new HttpClient(new StubHandler(new Dictionary<string, byte[]>
        {
            ["https://example/setup"] = payload,
            ["https://example/setup.sha256"] = System.Text.Encoding.ASCII.GetBytes($"{hash}  ProjectBridge-Setup.exe")
        }));
        var directory = Directory.CreateTempSubdirectory("lpb-update-").FullName;
        try
        {
            var update = new UpdateInfo(new Version(0, 2, 0), "v0.2.0", "https://example/r", "https://example/setup", "https://example/setup.sha256");
            var checker = new UpdateChecker(http);
            if (hashMatches)
            {
                var path = await checker.DownloadInstallerAsync(update, directory);
                Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => checker.DownloadInstallerAsync(update, directory));
                Assert.Empty(Directory.GetFiles(directory));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ParsesLatestRedirect()
    {
        var update = UpdateChecker.FromLatestRedirect("https://github.com/keros68/ProjectBridge/releases/tag/v0.1.7", new Version(0, 1, 6));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 1, 7), update.Version);
        Assert.Equal("https://github.com/keros68/ProjectBridge/releases/download/v0.1.7/ProjectBridge-Setup.exe", update.SetupUrl);
        Assert.Equal(update.SetupUrl + ".sha256", update.SetupHashUrl);
        Assert.Null(UpdateChecker.FromLatestRedirect("https://github.com/keros68/ProjectBridge/releases/tag/v0.1.6", new Version(0, 1, 6)));
        Assert.Throws<InvalidDataException>(() => UpdateChecker.FromLatestRedirect("https://github.com/keros68/ProjectBridge/releases", new Version(0, 1, 6)));
    }

    [Fact]
    public async Task FallsBackToReleasePageWhenApiFails()
    {
        var api = new HttpClient(new StubHandler(new Dictionary<string, byte[]>()));
        var redirect = new HttpClient(new RedirectHandler("https://github.com/keros68/ProjectBridge/releases/tag/v0.2.0"));
        var update = await new UpdateChecker(api, redirect).CheckAsync(new Version(0, 1, 6));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0), update.Version);
        Assert.True(update.CanInstall);
    }

    private sealed class RedirectHandler(string location) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri(location) } });
    }

    private sealed class StubHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responses.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
