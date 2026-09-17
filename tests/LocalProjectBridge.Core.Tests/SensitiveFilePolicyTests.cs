using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Tests;

public sealed class SensitiveFilePolicyTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("config/.env.production")]
    [InlineData("server/id_rsa")]
    [InlineData("keys/id_rsa.pem")]
    [InlineData("deploy/host.key")]
    [InlineData("certs/bundle.pfx")]
    [InlineData("certs/cert.p12")]
    [InlineData("tools/client.ppk")]
    [InlineData(".git-credentials")]
    [InlineData("config/.netrc")]
    [InlineData("npm/.npmrc")]
    [InlineData(".ssh/config")]
    [InlineData(".aws/credentials")]
    [InlineData(".azure/accessTokens.json")]
    [InlineData(".gcloud/credentials.db")]
    [InlineData(".config/gcloud/credentials")]
    [InlineData(".kube/config")]
    [InlineData(".docker/config.json")]
    [InlineData(".git/config")]
    [InlineData("vendor/repository/.git/config")]
    [InlineData("src/keys/.ssh/id_ed25519")]
    [InlineData("tools/cache/.aws/credentials")]
    [InlineData("nested/.config/gcloud/credentials.db")]
    [InlineData(".gnupg/pubring.kbx")]
    [InlineData("secrets.json")]
    [InlineData("config/secrets.yaml")]
    [InlineData("vault.keystore")]
    [InlineData("db.kdbx")]
    public void SensitivePaths_AreRejected(string relativePath)
        => Assert.True(SensitiveFilePolicy.IsSensitiveRelative(relativePath));

    [Theory]
    [InlineData("src/main.rs")]
    [InlineData("docs/architecture.md")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData("environment.ts")]
    [InlineData("src/id_rsa_example.txt")] // 前缀规则只匹配文件名，不匹配路径段中间
    [InlineData("keystores.md")]
    public void RegularProjectFiles_AreAllowed(string relativePath)
        => Assert.False(SensitiveFilePolicy.IsSensitiveRelative(relativePath));

    [Fact]
    public void SensitiveDirectoryName_IsRejectedAtAnyPathLevel()
    {
        Assert.True(SensitiveFilePolicy.IsSensitiveRelative("vendor/repository/.git/config"));
        Assert.True(SensitiveFilePolicy.IsSensitiveRelative("nested/keys/.ssh/config"));
        Assert.True(SensitiveFilePolicy.IsSensitiveRelative("cache/.aws/credentials"));
    }

    [Fact]
    public async Task CustomIgnoreFile_AddsDenyRules()
    {
        var root = Directory.CreateTempSubdirectory("lpb-ignore");
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(root.FullName, ".bridgeignore"),
                ["build/output", "*.keystore.b64", "internal/"]);
            var canonical = ProjectPathGuard.CanonicalizeProjectRoot(root.FullName);
            Assert.True(SensitiveFilePolicy.IsSensitive(canonical, Path.Combine(root.FullName, "build", "output", "a.txt")));
            Assert.True(SensitiveFilePolicy.IsSensitive(canonical, Path.Combine(root.FullName, "release.keystore.b64")));
            Assert.True(SensitiveFilePolicy.IsSensitive(canonical, Path.Combine(root.FullName, "internal", "doc.md")));
            Assert.False(SensitiveFilePolicy.IsSensitive(canonical, Path.Combine(root.FullName, "src", "main.rs")));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CommentsAndEmptyLines_AreIgnored()
    {
        var root = Directory.CreateTempSubdirectory("lpb-ignore2");
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(root.FullName, ".c2cignore"),
                ["# 注释", "", "secret-dir/"]);
            var canonical = ProjectPathGuard.CanonicalizeProjectRoot(root.FullName);
            Assert.True(SensitiveFilePolicy.IsSensitive(canonical, Path.Combine(root.FullName, "secret-dir", "x.txt")));
            Assert.False(SensitiveFilePolicy.IsSensitive(canonical, Path.Combine(root.FullName, "#comment-file")));
        }
        finally { root.Delete(recursive: true); }
    }
}
