using System.Runtime.InteropServices;
using System.Text;

namespace AlhPro.Core;

/// <summary>
/// 纯逻辑工具(无 WinUI / 无 System.Drawing 依赖,可被普通单元测试项目直接引用)。
/// 把这些函数从主项目抽出来,是为了让它们能脱离 WinUI 应用单独测试——
/// 否则主项目(WinExe + WindowsAppSDK)引用进测试项目会因 SDK 版本耦合而无法构建。
/// </summary>
public static class PathUtil
{
    /// <summary>
    /// 把含非 ASCII(中文/特殊字符)的路径转换为 8.3 短路径,规避 ffmpeg 在 GBK 代码页下的
    /// "Illegal byte sequence" 乱码/失败。纯 ASCII 路径原样返回;输出文件未创建时缩短其父目录再拼文件名。
    /// </summary>
    public static string FfmpegSafePath(string path)
    {
        try
        {
            bool needShort = false;
            foreach (var c in path) if (c > 127) { needShort = true; break; }
            if (!needShort) return path;
            var sb = new StringBuilder(512);
            uint r = GetShortPathName(path, sb, 512);
            if (r > 0) return sb.ToString();
            // 输出文件往往【还没创建】,GetShortPathName 对不存在的路径会失败 → 缩短父目录(必然存在)再拼文件名
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(name))
            {
                var sb2 = new StringBuilder(512);
                uint rd = GetShortPathName(dir, sb2, 512);
                if (rd > 0) return Path.Combine(sb2.ToString(), name);
            }
            return path;
        }
        catch { return path; }
    }

    /// <summary>向上取整到不小于 n 的 2 的幂(视频/图片超分倍率用,引擎只支持 2 的幂级联)。</summary>
    public static int CeilPowerOfTwo(double n)
    {
        int p = 1;
        while (p < n) p *= 2;
        return p;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}
