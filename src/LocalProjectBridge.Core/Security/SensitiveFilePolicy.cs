using System.Text.RegularExpressions;

namespace LocalProjectBridge.Core.Security;

/// <summary>
/// 敏感文件拒绝规则（设计文档 6.3）：`.env` 变体、私钥与证书私钥、云凭据目录、
/// Git 凭据与系统密钥存储，以及用户通过 `.c2cignore` / `.bridgeignore` 补充的路径。
/// 路径匹配统一使用 `/` 分隔并忽略大小写。
/// </summary>
public static class SensitiveFilePolicy
{
    private const string IgnoreFileC2C = ".c2cignore";
    private const string IgnoreFileProduct = ".bridgeignore";

    /// <summary>整个目录下的内容一律拒绝（按相对路径段比较）。</summary>
    private static readonly string[] SensitiveDirectories =
    [
        ".git",       // Git 元数据可能包含带凭据的 remote；Git 信息经由 Git 命令工具提供
        ".ssh",
        ".aws",
        ".azure",
        ".gcloud",
        ".kube",
        ".docker",
        ".gnupg",
        ".password-store",
        ".config/gcloud",
        ".config/gh"
    ];

    /// <summary>精确匹配的相对路径或文件名。</summary>
    private static readonly string[] SensitiveExactNames =
    [
        ".env",
        ".git-credentials",
        ".netrc",
        ".htpasswd",
        ".npmrc",
        ".yarnrc.yml",
        ".pypirc",
        "credentials.json",
        "secrets.json",
        "secrets.yaml",
        "secrets.yml",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
        "id_rsa",
        "keystore",
        "known_hosts"
    ];

    /// <summary>文件名前缀匹配。</summary>
    private static readonly string[] SensitiveNamePrefixes =
    [
        ".env.",
        "id_dsa.",
        "id_ecdsa.",
        "id_ed25519.",
        "id_rsa.",
        "keystore."
    ];

    /// <summary>扩展名匹配（私钥与证书私钥）。</summary>
    private static readonly string[] SensitiveExtensions =
    [
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".ppk",
        ".jks",
        ".kdbx",
        ".keystore"
    ];

    public static bool IsSensitive(string canonicalProjectRoot, string fullPath)
        => IsSensitiveRelative(
            ProjectPathGuard.GetRelativePathInsideProject(canonicalProjectRoot, fullPath),
            LoadIgnoreRules(canonicalProjectRoot));

    public static bool IsSensitiveRelative(string relativePath, IReadOnlyList<IgnoreRule>? extraRules = null)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        if (normalized.Length == 0) return false;
        var segments = normalized.Split('/');

        foreach (var directory in SensitiveDirectories)
        {
            var directorySegments = directory.Split('/');
            for (var start = 0; start <= segments.Length - directorySegments.Length; start++)
            {
                if (segments.AsSpan(start, directorySegments.Length).SequenceEqual(directorySegments))
                    return true;
            }
        }

        var name = segments[^1];
        if (SensitiveExactNames.Contains(name)) return true;
        if (SensitiveNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) return true;
        if (SensitiveExtensions.Any(extension => name.EndsWith(extension, StringComparison.Ordinal))) return true;

        if (extraRules is not null)
            foreach (var rule in extraRules)
                if (rule.Matches(normalized)) return true;

        return false;
    }

    /// <summary>读取项目根目录下用户自定义的忽略规则（`.c2cignore` 与 `.bridgeignore`）。</summary>
    public static IReadOnlyList<IgnoreRule> LoadIgnoreRules(string canonicalProjectRoot)
    {
        var rules = new List<IgnoreRule>();
        foreach (var fileName in (ReadOnlySpan<string>)[IgnoreFileC2C, IgnoreFileProduct])
        {
            var path = Path.Combine(canonicalProjectRoot, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    if (trimmed.StartsWith('!')) continue; // 不支持反选规则：宁可误拒，不可误放
                    rules.Add(IgnoreRule.Parse(trimmed));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return rules;
    }

    /// <summary>简化版 ignore 规则：`/` 开头表示项目根相对，`/` 结尾仅作用于目录；`*` 与 `?` 为通配符。规则命中路径本身及其下所有内容。</summary>
    public sealed class IgnoreRule
    {
        private readonly Regex _pattern;

        private IgnoreRule(Regex pattern) => _pattern = pattern;

        public static IgnoreRule Parse(string raw)
        {
            var glob = raw.Replace('\\', '/').Trim().ToLowerInvariant().Trim('/').TrimStart('/');
            var builder = new System.Text.StringBuilder();
            foreach (var character in glob)
            {
                switch (character)
                {
                    case '*': builder.Append("[^/]*"); break;
                    case '?': builder.Append("[^/]"); break;
                    default: builder.Append(Regex.Escape(character.ToString())); break;
                }
            }
            return new IgnoreRule(new Regex($"^{builder}($|/)", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant));
        }

        public bool Matches(string normalizedRelativePath)
            => _pattern.IsMatch(normalizedRelativePath)
               || _pattern.IsMatch(normalizedRelativePath + "/x");
    }
}
