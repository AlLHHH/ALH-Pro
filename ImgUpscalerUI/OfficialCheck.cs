// OfficialCheck.cs — 正版校验(轻量防篡改):
// 官方发布的安装包在程序目录带 OFFICIAL.txt(内容= 版本号+exe 哈希签名标记);
// 软件启动时读取校验,缺失/不符 → 提示"疑似修改版"(警告但不阻止使用,不伤害正版用户)。
// 不做联网授权/激活码(免费工具,不坑用户);只是让"二次打包/盗版分发"露馅。
using System;
using System.IO;

namespace ALHPro;

public static class OfficialCheck
{
    /// <summary>官方标记行(打包时由 build 脚本写入 发布版\OFFICIAL.txt)。</summary>
    public static string MarkerLine => $"ALHPro-Official-{UpdateChecker.CurrentVersion}";

    /// <summary>校验:通过=true(正版);失败=false(疑似修改版/缺失标记)。只检查标记存在+版本匹配,不校验 exe 哈希(签名链复杂,从简)。</summary>
    public static bool Verify()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "OFFICIAL.txt");
            if (!File.Exists(p)) return false;
            var content = File.ReadAllText(p).Trim();
            return content.Contains(MarkerLine, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>写标记(构建脚本/发布用;程序内一般不调用,除非"重新激活")。</summary>
    public static void WriteMarker(string dir)
    {
        try
        {
            File.WriteAllText(Path.Combine(dir, "OFFICIAL.txt"),
                $"ALH Pro 官方发布标记\n{MarkerLine}\n请勿修改/移除本文件(正版校验用)");
        }
        catch { }
    }
}
