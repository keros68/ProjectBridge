using System.Text;

namespace LocalProjectBridge.Core.Collaboration;

public sealed record CodexSkillInstallResult(string Destination, bool Installed, string Message);

public static class CodexSkillInstaller
{
    public const string SkillName = "projectbridge-chatgpt";
    private const string RelayPlaceholder = "{{PROJECTBRIDGE_RELAY_PATH}}";

    public static CodexSkillInstallResult Install(
        string applicationDirectory,
        string? userProfileDirectory = null)
    {
        var appDirectory = Path.GetFullPath(applicationDirectory);
        var source = Path.Combine(appDirectory, "skills", SkillName, "SKILL.md");
        var relay = Path.Combine(appDirectory, "ProjectBridge.Relay.exe");
        if (!File.Exists(source)) throw new FileNotFoundException("程序包缺少 Codex 协作 Skill。", source);
        if (!File.Exists(relay)) throw new FileNotFoundException("程序包缺少 ProjectBridge.Relay.exe。", relay);

        var profile = userProfileDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var destinationDirectory = Path.Combine(Path.GetFullPath(profile), ".codex", "skills", SkillName);
        var destination = Path.Combine(destinationDirectory, "SKILL.md");
        if (File.Exists(destination))
            return new(destination, false, "检测到已安装的 Skill；已保留用户现有版本。若需更新，请先自行备份或移走该目录。");

        var template = File.ReadAllText(source, new UTF8Encoding(false, true));
        if (!template.Contains(RelayPlaceholder, StringComparison.Ordinal))
            throw new InvalidDataException("Skill 模板缺少 Relay 路径占位符。");
        var content = template.Replace(RelayPlaceholder, relay, StringComparison.Ordinal);
        Directory.CreateDirectory(destinationDirectory);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        return new(destination, true, "Codex 协作 Skill 已安装；新 Codex 任务会自动发现它。");
    }
}
