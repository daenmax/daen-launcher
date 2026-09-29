using System.Diagnostics;
using System.Runtime.InteropServices;
using DaenLauncher.Models;
using Microsoft.Win32;

namespace DaenLauncher.Services;

/// <summary>
/// 开机自启服务（需求-常规第2条）：
/// 支持两种方式：1）写注册表 HKCU\...\Run；2）在启动文件夹 shell:startup 放快捷方式。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DaenLauncher";
    private const string ShortcutFileName = "Daen Launcher.lnk";

    /// <summary>
    /// 应用自启设置（根据设置里的开关和方式，启用或取消自启）。
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        try
        {
            if (settings.AutoStart)
            {
                if (settings.AutoStartMode == AutoStartMode.Registry)
                {
                    RemoveStartupShortcut();
                    SetRegistryAutoStart();
                }
                else
                {
                    RemoveRegistryAutoStart();
                    SetStartupFolderShortcut();
                }
            }
            else
            {
                RemoveRegistryAutoStart();
                RemoveStartupShortcut();
            }
        }
        catch
        {
            // 自启设置失败不阻塞软件运行
        }
    }

    /// <summary>写注册表自启</summary>
    private static void SetRegistryAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        key?.SetValue(ValueName, $"\"{GetExePath()}\"");
    }

    /// <summary>删除注册表自启</summary>
    private static void RemoveRegistryAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key?.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName, false);
        }
    }

    /// <summary>在启动文件夹创建快捷方式（COM IShellLink）</summary>
    private static void SetStartupFolderShortcut()
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var lnkPath = Path.Combine(startupDir, ShortcutFileName);

        // 已存在且目标正确就不再创建
        if (File.Exists(lnkPath))
        {
            var resolved = LnkResolver.Resolve(lnkPath);
            if (resolved != null &&
                resolved.Value.Target.Equals(GetExePath(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var shellLink = (LnkResolver.IShellLinkW)new LnkResolver.ShellLink();
        shellLink.SetPath(GetExePath());
        shellLink.SetDescription(AppInfoService.Config.AppDescription ?? "");
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(GetExePath()) ?? "");
        var persistFile = (LnkResolver.IPersistFile)shellLink;
        persistFile.Save(lnkPath, true);
        Marshal.ReleaseComObject(shellLink);
    }

    /// <summary>删除启动文件夹里的快捷方式</summary>
    private static void RemoveStartupShortcut()
    {
        try
        {
            var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var lnkPath = Path.Combine(startupDir, ShortcutFileName);
            if (File.Exists(lnkPath)) File.Delete(lnkPath);
        }
        catch
        {
            // 删除失败忽略
        }
    }

    /// <summary>获取当前 exe 完整路径</summary>
    private static string GetExePath() => Environment.ProcessPath ?? AppContext.BaseDirectory;
}
