// CroppedStorage.cs — 裁剪产物专用存储(应用私有数据目录)
// 裁剪结果只存在于右侧列表(供后续放大/抠图输出),不写入源图目录/桌面;
// 应用启动时清理历史文件,避免累积。
using System;
using System.IO;

namespace ALHPro;

public static class CroppedStorage
{
    private static string DirPath => Path.Combine(ParaPaths.CacheDir, "cropped");

    public static string Dir
    {
        get
        {
            Directory.CreateDirectory(DirPath);
            return DirPath;
        }
    }

    /// <summary>清理历史裁剪文件(应用启动时调用)。</summary>
    public static void Clean()
    {
        try
        {
            if (Directory.Exists(DirPath))
            {
                foreach (var f in Directory.EnumerateFiles(DirPath))
                    File.Delete(f);
            }
        }
        catch { /* 清理失败不影响使用 */ }
    }
}
