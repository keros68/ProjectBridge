using Microsoft.Win32;

namespace LocalProjectBridge;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ProjectBridge";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的启动项设置。");
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }
}
