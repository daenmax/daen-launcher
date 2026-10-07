using System;

namespace DaenLauncher.Services;

/// <summary>
/// Windows Shell 的固定常量（known folder GUID、显示名格式标志等）。
/// 这些值由 Windows SDK 定义，永久不变，集中放在这里避免散落。
/// </summary>
internal static class ShellConstants
{
    /// <summary>FOLDERID_AppsFolder 的 GUID：shell 命名空间里的"应用程序"文件夹（即 shell:AppsFolder）</summary>
    public const string AppsFolderGuidString = "1e87508d-89c2-42f0-8a7e-645a0f50ca58";

    /// <summary>FOLDERID_AppsFolder 的 Guid 形式（SHGetKnownFolderPath 用）</summary>
    public static readonly Guid AppsFolderGuid = new(AppsFolderGuidString);

    /// <summary>AppsFolder 的解析名形式："::{GUID}"（SHParseDisplayName 可直接解析）</summary>
    public const string AppsFolderMoniker = "::{" + AppsFolderGuidString + "}";

    /// <summary>SHGDN_NORMAL：显示名（用户可见的本地化名称）</summary>
    public const uint ShGdnNormal = 0x0;

    /// <summary>SHGDN_FORPARSING：解析名（AppsFolder 子项 = AUMID，形如 包家族名!应用ID）</summary>
    public const uint ShGdnForParsing = 0x8000;

    /// <summary>SHCONTF_FOLDERS：枚举时包含文件夹项</summary>
    public const uint ShContfFolders = 0x20;

    /// <summary>SHCONTF_NONFOLDERS：枚举时包含非文件夹项</summary>
    public const uint ShContfNonFolders = 0x40;
}
