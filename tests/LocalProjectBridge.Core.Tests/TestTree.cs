namespace LocalProjectBridge.Core.Tests;

/// <summary>测试临时目录清理：先移除联接/符号链接（悬空联接会让 Directory.Delete 递归报访问拒绝），再清除 git 只读文件属性。</summary>
internal static class TestTree
{
    public static void Delete(string root)
    {
        try
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string[] directories;
                try { directories = Directory.GetDirectories(current); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var directory in directories)
                {
                    var info = new DirectoryInfo(directory);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        try { info.Delete(); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    else pending.Push(directory);
                }
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
