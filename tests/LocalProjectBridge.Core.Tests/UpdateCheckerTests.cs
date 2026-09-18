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
}
