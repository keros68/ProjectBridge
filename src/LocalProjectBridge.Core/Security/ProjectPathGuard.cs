namespace LocalProjectBridge.Core.Security;

/// <summary>
/// 项目路径防护：规范化项目根目录，阻止通过 `..`、符号链接、目录联接和重解析点
/// 访问项目根之外的路径（设计文档 6.3）。
/// 所有比较使用最终路径（final path）并忽略 Windows 大小写差异。
/// </summary>
public static class ProjectPathGuard
{
    public static string CanonicalizeProjectRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("项目路径为空，请重新选择项目文件夹。");
        var fullPath = Path.GetFullPath(path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"项目目录不存在或已被移动：{fullPath}");
        // 项目根本身若是目录联接或符号链接，沙箱以解析后的真实目录为准。
        return ResolveFinalPath(fullPath) ?? fullPath;
    }

    /// <summary>解析 NTFS 最终路径，折叠符号链接、目录联接和subst 映射；失败时返回 null 由调用方回退。</summary>
    public static unsafe string? ResolveFinalPath(string path)
    {
        // 0x02000000 = FILE_FLAG_BACKUP_SEMANTICS：允许打开目录句柄
        const int FileFlagBackupSemantics = 0x02000000;
        var handle = NativeMethods.CreateFile(
            longPathPrefix + path.Replace('/', Path.DirectorySeparatorChar),
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid) return null;
        try
        {
            const int initialLength = 1024;
            var buffer = (char*)Marshal.AllocHGlobal(initialLength * sizeof(char));
            try
            {
                var length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, initialLength, 0);
                if (length <= 0) return null;
                if (length > initialLength)
                {
                    Marshal.FreeHGlobal((IntPtr)buffer);
                    buffer = (char*)Marshal.AllocHGlobal(length * sizeof(char));
                    length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, length, 0);
                    if (length <= 0) return null;
                }
                var resolved = new string(buffer, 0, (int)length);
                return NormalizeFinalPath(resolved);
            }
            finally { Marshal.FreeHGlobal((IntPtr)buffer); }
        }
        finally { handle.Dispose(); }
    }

    private static string NormalizeFinalPath(string resolved)
    {
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + resolved[8..];
        if (resolved.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            return resolved[4..];
        return resolved;
    }

    private const string longPathPrefix = @"\\?\";

    /// <summary>判断 candidate 是否位于 canonicalRoot 内部（含根本身）。candidate 可以不存在，此时退化为词法判断。</summary>
    public static bool IsInsideProject(string canonicalRoot, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(canonicalRoot) || string.IsNullOrWhiteSpace(candidatePath)) return false;
        var candidate = Path.GetFullPath(candidatePath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var root = Path.GetFullPath(canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var final = ResolveFinalPath(candidate) ?? ResolveThroughExistingAncestor(candidate);
        var compare = final ?? candidate;
        if (string.Equals(compare, root, StringComparison.OrdinalIgnoreCase)) return true;
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return compare.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>candidate 不在项目内时抛出异常，并拒绝显式的 `..` 逃逸片段。</summary>
    public static void EnsureInsideProject(string canonicalRoot, string candidatePath)
    {
        var trimmed = candidatePath.Trim();
        if (trimmed.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("..", StringComparer.Ordinal))
            throw new UnauthorizedAccessException($"路径包含不允许的相对片段：{RedactPath(trimmed)}");
        if (!IsInsideProject(canonicalRoot, candidatePath))
            throw new UnauthorizedAccessException($"拒绝访问项目之外的路径：{RedactPath(candidatePath)}");
    }

    /// <summary>
    /// Resolves the nearest existing ancestor and appends the still-missing suffix.
    /// This prevents a non-existent child below a junction from falling back to a
    /// misleading lexical path inside the project.
    /// </summary>
    private static string? ResolveThroughExistingAncestor(string fullPath)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(fullPath);
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            var name = Path.GetFileName(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return null;
            missing.Push(name);
            current = parent;
        }
        var resolved = ResolveFinalPath(current);
        if (resolved is null) return null;
        while (missing.Count > 0) resolved = Path.Combine(resolved, missing.Pop());
        return Path.GetFullPath(resolved);
    }

    /// <summary>计算相对路径；路径在项目外时抛出异常。</summary>
    public static string GetRelativePathInsideProject(string canonicalRoot, string fullPath)
    {
        if (!IsInsideProject(canonicalRoot, fullPath))
            throw new UnauthorizedAccessException($"拒绝访问项目之外的路径：{RedactPath(fullPath)}");
        var final = ResolveFinalPath(fullPath) ?? Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(canonicalRoot, final);
        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>错误信息中只保留路径末段，避免诊断信息泄露完整目录结构。</summary>
    public static string RedactPath(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name
        ? name
        : "[路径已隐藏]";

    internal static class NativeMethods
    {
        private const string Kernel32 = "kernel32.dll";

        [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, EntryPoint = "CreateFileW")]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string fileName,
            int desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            int flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(Kernel32, SetLastError = true, ExactSpelling = true, EntryPoint = "GetFinalPathNameByHandleW")]
        internal static extern unsafe int GetFinalPathNameByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle file,
            char* filePath,
            int filePathLength,
            int flags);
    }
}
