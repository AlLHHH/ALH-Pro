using System;
using System.IO;

namespace ALHPro;

/// <summary>
/// 用户数据路径统一约定(2026-08-30 整理):
///   ALHPro\diagnostic.log      —— 日志(唯一留在根目录的)
///   ALHPro\settings\*.json/txt —— 所有配置、状态、记忆文件(用户要求日志目录干净)
///   ALHPro\cache\cropped       —— 可再生的缓存(裁剪临时图)
/// 各文件读写都经此获取路径;旧版根目录文件读取时自动回退(防用户设置丢失)。
/// </summary>
public static class ParaPaths
{
    public static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALHPro");

    /// <summary>设置/状态子目录(配置都放这里)。</summary>
    public static string SettingsDir => Path.Combine(AppRoot, "settings");

    /// <summary>缓存子目录(可重新生成的数据)。</summary>
    public static string CacheDir => Path.Combine(AppRoot, "cache");

    /// <summary>设置文件路径:返回"新子目录优先,旧版根目录文件自动回退"的路径。
    /// 若旧文件存在且新位置不存在 → 迁移(移动)到新位置,之后都读写新位置。</summary>
    public static string SettingsFile(string fileName)
    {
        try
        {
            var fresh = Path.Combine(SettingsDir, fileName);
            var legacy = Path.Combine(AppRoot, fileName);
            if (File.Exists(fresh)) return fresh;
            if (File.Exists(legacy))
            {
                Directory.CreateDirectory(SettingsDir);
                File.Move(legacy, fresh, overwrite: true);   // 迁移一次,以后走新位置
                return fresh;
            }
            Directory.CreateDirectory(SettingsDir);
            return fresh;
        }
        catch
        {
            // 迁移失败(权限等)→ 仍用新路径(下次写入会创建)
            return Path.Combine(SettingsDir, fileName);
        }
    }
}
