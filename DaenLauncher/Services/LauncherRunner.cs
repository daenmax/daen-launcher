using System.Diagnostics;
using DaenLauncher.Models;

namespace DaenLauncher.Services;

/// <summary>
/// 项目启动器：根据项目类型启动对应目标（需求-软件界面说明5：
/// 点击项目后用系统默认关联的应用打开，文件夹用资源管理器打开等）。
/// </summary>
public static class LauncherRunner
{
    /// <summary>
    /// 启动项目。返回是否启动成功。
    /// </summary>
    /// <param name="item">项目</param>
    /// <param name="asAdmin">是否以管理员身份运行</param>
    public static bool Start(LauncherItem item, bool asAdmin = false)
    {
        try
        {
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path)) return false;

            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                Arguments = item.Arguments ?? ""
            };

            switch (item.Type)
            {
                case LauncherItemType.Exe:
                case LauncherItemType.Lnk:
                case LauncherItemType.File:
                    psi.FileName = path;
                    // 管理员权限：项目勾选了"以管理员身份运行"，或调用方临时要求管理员（右键菜单）
                    if (asAdmin || item.RunAsAdmin) psi.Verb = "runas";
                    // 把工作目录设为程序所在目录。有些程序用相对路径找自己的资源文件
                    // （如字体、配置），不设的话工作目录是启动器自己的目录，会找不到文件。
                    // .lnk 快捷方式由系统解析目标自带的工作目录，不需要设置。
                    if (item.Type != LauncherItemType.Lnk)
                    {
                        var dir = System.IO.Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            psi.WorkingDirectory = dir;
                        }
                    }
                    break;

                case LauncherItemType.Folder:
                    psi.FileName = "explorer.exe";
                    psi.Arguments = $"\"{path}\"";
                    break;

                case LauncherItemType.Url:
                case LauncherItemType.Protocol:
                    psi.FileName = path;
                    break;

                case LauncherItemType.Uwp:
                    // UWP 应用通过 shell:AppsFolder\<应用ID> 启动
                    var appId = path.StartsWith(LauncherItemPaths.UwpPrefix, StringComparison.OrdinalIgnoreCase)
                        ? path[LauncherItemPaths.UwpPrefix.Length..]
                        : path;
                    psi.FileName = $"{LauncherItemPaths.UwpPrefix}{appId}";
                    break;

                default:
                    return false;
            }

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在资源管理器中打开项目所在位置</summary>
    public static void OpenContainingFolder(LauncherItem item)
    {
        try
        {
            var path = item.Path;
            if (item.Type == LauncherItemType.Uwp || item.Type == LauncherItemType.Url ||
                item.Type == LauncherItemType.Protocol)
            {
                return; // UWP/URL 没有本地位置
            }

            if (item.Type == LauncherItemType.Folder)
            {
                // 文件夹：选中它自己
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
                return;
            }

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
            }
        }
        catch
        {
            // 打开失败忽略
        }
    }

    /// <summary>复制项目完整路径到剪贴板</summary>
    public static async void CopyFullPath(LauncherItem item)
    {
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(item.Path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
        catch
        {
            // 剪贴板访问失败
        }
        await Task.CompletedTask;
    }
}
