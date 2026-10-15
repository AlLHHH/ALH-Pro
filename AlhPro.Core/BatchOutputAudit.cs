namespace AlhPro.Core;

/// <summary>
/// 引擎**批量(目录模式)**输出的"输入帧 ↔ 输出帧"对齐审计(纯逻辑,不碰文件系统 ⇒ 可单测)。
///
/// 【为什么要它】引擎退出码 0、却少写若干帧时,旧代码只判一条"输出目录里一个文件都没有"就放行:
/// 少产的那几帧会一路静默走到合帧/编码(按 `frame_%06d` 连续编号读),表现为"成片缺帧/某几秒对不上",
/// 而日志里查不到任何线索。真机实测(2026-10-15,RTX 4060 Laptop)就有这种形态:引擎 `-t 384` 的目录批处理
/// **同批里好帧与黑帧混排、退出码仍为 0**(老版 2022 exe;2026 重编版未见)。
/// 【2026-10-15 起】调用方(`VideoService` 的超分批)用本类对账,缺帧走"源帧缩放兜底"(尺寸对齐)补齐,不再少帧。
///
/// 【为什么不能只比文件名】引擎对**带 alpha 的输入**直出 JPG 时会把输出名改成 `xxx.jpg.png`
/// (见 `ImgUpscalerUI/EngineService.EnsureFinalOutput` 的注释),宿主侧再 `Path.ChangeExtension` 还可能得到
/// `xxx.jpg.jpg`。所以这里按"**剥掉末尾所有已知图片扩展名后的主名**"匹配,而不是比完整文件名。
/// 帧目录内命名统一(`frame_%06d.png` / `.jpg`),不存在"同名不同格式"的份额,主名口径足够且不误判。
///
/// 【判据边界 —— 刻意不做的事】只回答"某个输入帧有没有对应产出",**不做任何像素级判黑**:
/// 视频批量路径自 2026-09-27 起明令禁止事后判黑(黑色转场/夜戏会被误判 ⇒ 整条任务被迫转 ONNX 慢路),
/// 留档见 `ImgUpscalerUI/VideoService.cs` 的「源帧黑场豁免判据删除」段与 `AlhPro.Core.FrameInspect` 的类注释。
/// </summary>
public static class BatchOutputAudit
{
    /// <summary>参与匹配的图片扩展名(剥名与"输出是不是帧图"两处共用同一份清单)。</summary>
    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff" };

    /// <summary>名字是否以已知图片扩展名结尾(用来把引擎顺手写的日志/说明文件排除在"产出帧"之外)。</summary>
    public static bool IsImageName(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName ?? "");
        foreach (var ext in ImageExts)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>剥掉末尾**所有**已知图片扩展名:`frame_000001.png` → `frame_000001`;
    /// `frame_000001.jpg.png`(带 alpha 的输入直出 JPG 时引擎的改名)→ `frame_000001`;
    /// 非图片名(如 `readme.txt`)原样返回。空/仅扩展名时返回空串(调用方跳过)。</summary>
    public static string FrameStem(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName ?? "");
        while (name.Length > 0)
        {
            int cut = -1;
            foreach (var ext in ImageExts)
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { cut = ext.Length; break; }
            if (cut < 0) break;
            name = name.Substring(0, name.Length - cut);
        }
        return name;
    }

    /// <summary>列出"输入里有、产出里没有"的那些输入(**按传入顺序、原名返回**,便于日志直接念出帧号)。
    /// 【口径】输出侧只认图片名(`IsImageName`),主名大小写不敏感;零字节产出由调用方先滤掉(本类不看文件)。
    /// 输入侧不做图片名过滤:批量输入目录本身就是拆帧产物。</summary>
    public static IReadOnlyList<string> MissingOutputs(
        IEnumerable<string> inputPaths, IEnumerable<string> outputPaths)
    {
        var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (outputPaths != null)
        {
            foreach (var o in outputPaths)
            {
                if (!IsImageName(o)) continue;
                var stem = FrameStem(o);
                if (stem.Length > 0) produced.Add(stem);
            }
        }
        var missing = new List<string>();
        if (inputPaths == null) return missing;
        foreach (var i in inputPaths)
        {
            var stem = FrameStem(i);
            if (stem.Length == 0) continue;      // 拿不准就不报缺失(与 FrameInspect"total≤0 不误判"同思路)
            if (!produced.Contains(stem)) missing.Add(i);
        }
        return missing;
    }
}
