using System.Globalization;

namespace AlhPro.Core;

/// <summary>视频后处理滤镜链的构造(纯字符串逻辑,从 VideoService.BuildPostFilter 抽出、可单测)。
/// 【为什么抽出来】① 这条链直接决定成片画面,而"看着等价"的合并/改写会静默改画面 —— 必须用单测把
/// 每一档产出的 ffmpeg 滤镜字符串【逐字】钉住;② 抽成纯函数才能把"能不能合并"这件事用测试说清楚。
/// 【口径(参数 0-100,0=关;2026-09 全部重做,每档用不同机制,不许再退回"同一个 unsharp 换半径")】
///   · 锐化(smartblur 负强度, r=1, 带阈值 3/6)——细节反锐化,阈值保护平坦区与噪点。
///     实测 v50:PSNR −0.35 dB、edgePSNR −0.38(旧版 unsharp 5x5 a1.5 是 −3.77 / −4.86 ✗)。
///   · 清晰(unsharp 13x13,低强度)——大半径"局部对比",只动中调不通吃边缘。
///     (ffmpeg 的 unsharp 【没有阈值参数】,第 6 个参数是色度强度,所以只能靠低强度控制副作用)
///   · 钝化蒙版(smartblur 负强度, r=2, 阈值 8)——只锐化超过阈值的明显边缘;
///     实测 v50 edgeSSIM 0.9582 ≥ 基底 0.9571,是唯一不伤边缘结构的档,故预设里给得最多。
///   · 保留细节(cas,对比度自适应)——按局部对比自适应增益,设计上不产生白边;
///     实测 v50 平坦区误差 2.09(基底 2.07),全档最干净。
///   · 【已移除:去模糊】ffmpeg 没有反卷积滤镜(实测卷积核方案 PSNR −8.6 dB ✗),视频里那档
///     只是"大半径锐化",名不副实 → 删除(图片页的「去模糊」是真·Richardson-Lucy 反卷积,那边保留)。
///   · 【已移除:边缘抗锯齿(ffmpeg sab)】2026-09-12 实测:4K 下整条 6 档链 = 4.88 秒/帧,
///     去掉 sab = 0.10 秒/帧(约 48 倍),sab 单跑也要 0.90 秒/帧。同一效果改用 C# 实现
///     (AlhPro.Core.EdgeSmooth,在"PNG→JPG 统一"那一步顺带做),故本函数【不再产出】任何 AA 滤镜;
///     aa 形参保留只为调用方签名不变。
/// 【为什么这几档【不能】合并成更少的滤镜实例(2026-09-13 评估结论,附理由,别再试)】
///   ① 两档 smartblur 用的是【不同半径】(r=1 与 r=2)+ 不同阈值(3/6 与 8),而 ffmpeg 的 smartblur
///      每个实例只有一组 luma_radius/strength/threshold —— 没有多尺度形式,无法用一个实例表达两者的复合;
///      用"合并强度 + 单一半径"是换了核,逐像素结果必然不同。
///   ② 用 unsharp 取代 smartblur 也不行:unsharp 没有阈值参数(见上),而这两档的价值恰恰在
///      "阈值保护平坦区/噪点"(阈值 3/6/8),去掉阈值等于把平坦区的噪点一起放大 —— 画面会变。
///   ③ cas 是对比度自适应算法,没有等价滤镜;即使两档都是 unsharp,同核多趟也不能合并成一趟
///      (out = in + a·(in − blur(in)) 复合后会出现 a²·blur(d) 项,且中间 8-bit 钳制不可交换)。
///   ④ 这条链本来就只跑【一次 ffmpeg】(拼成一个 -vf 串,一次解码一次编码),所以"减少滤镜趟数"在
///      ffmpeg 调用层面已经是最小值 1;能省的只有每帧的滤镜计算量,而那只能靠改画面或换实现。
/// 【本文件不做的事】不做任何合并/近似 —— 需要改画面或换实现的方案一律由用户看 A/B 对比后决定。</summary>
public static class VideoPostFilters
{
    /// <summary>按强度构造后处理滤镜链(顺序 = 锐化 → 清晰 → 钝化蒙版 → 保留细节);全为 0 时返回 null。
    /// 参数范围 0-100,超出按上限钳制(与旧实现一致:锐化 ≤1.00、清晰 ≤0.50、钝化 ≤1.00、细节 ≤0.60)。</summary>
    public static string? Build(int sharpen, int clarity, int usm, int detail, int aa = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new System.Collections.Generic.List<string>();
        if (sharpen > 0)
            parts.Add($"smartblur=luma_radius=1:luma_strength=-{Math.Min(1.0, sharpen / 100.0).ToString("0.00", inv)}:luma_threshold={(sharpen <= 60 ? 3 : 6)}");
        if (clarity > 0)
            parts.Add($"unsharp=13:13:{Math.Min(0.50, clarity / 100.0 * 0.50).ToString("0.00", inv)}:13:13:0");
        if (usm > 0)
            parts.Add($"smartblur=luma_radius=2:luma_strength=-{Math.Min(1.0, usm / 100.0).ToString("0.00", inv)}:luma_threshold=8");
        if (detail > 0)
            parts.Add($"cas=strength={Math.Min(0.60, detail / 100.0 * 0.60).ToString("0.00", inv)}");
        // 边缘抗锯齿不再产出 ffmpeg 滤镜(见类注释);aa 只保留形参以免调用方签名变化。
        _ = aa;
        return parts.Count > 0 ? string.Join(",", parts) : null;
    }
}
