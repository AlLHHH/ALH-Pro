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
    /// <summary>**每一档的"安全上限"**(滑块 100 对应的实际滤镜强度)。
    /// 【2026-09-20 实测重新定标 · 为什么改】用户反馈"后处理加多了效果很奇怪、反倒很多噪点",并指出
    /// "调到 100 其实只有 50 的量"(确实:清晰与保留细节原来在 100 处只给到上限的 50%/60%)。
    /// 我用同一帧(素材(13) 第 20 秒,源:平坦噪声 0.32 / detail 1176 / 边宽 6.89px)把每一档
    /// 拉到 40/60/80/100 量了一遍(脚本 `_qa\post_filter_audit.py`),结论:
    ///   · 锐化 与 边缘增强:≤60 时噪声 0.98~1.02×(几乎零代价)⇒ 上限取 0.70;
    ///   · 钝化蒙版:60 就 1.42×、100 到 **2.03×**(最会出噪点的一档)⇒ 上限收到 **0.50**;
    ///   · 保留细节(cas):40 就已经 1.63×,但它是**唯一能把边缘变窄**的档(6.68px)⇒ 上限收到 0.30;
    ///   · 清晰(13x13 unsharp):噪声不涨,但**边缘越用越宽**(40→7.75px、100→9.83px,基线 6.89)⇒ 上限收到 0.25。
    /// ⇒ 现在滑块 **100 = 该档的安全上限**(不再出现"100 只有一半"或"100 直接把画面搞脏")。
    /// ⚠ 老设置/老预设必须按 <see cref="MigrateStrength"/> 做等效换算,否则同一份预设的画面会变(本仓库禁止静默改画面)。</summary>
    public static readonly IReadOnlyDictionary<string, double> SafeMax = new Dictionary<string, double>
    {
        ["sharpen"] = 0.70,     // OCR:滑块 100 → smartblur 负强度 0.70
        ["clarity"] = 0.25,     // 滑块 100 → unsharp 13x13 强度 0.25
        ["usm"] = 0.50,         // 滑块 100 → smartblur(r=2,thr=8) 负强度 0.50
        ["detail"] = 0.30,      // 滑块 100 → cas 强度 0.30
        ["edge"] = 0.70,        // 滑块 100 → smartblur(r=1,thr=8) 负强度 0.70
    };

    /// <summary>旧刻度(2026-09-15~2026-09-20)每一档在滑块 100 处给的实际强度 —— 只用于**老设置的等效迁移**。
    /// 迁移公式:新值 = 旧值 × (旧上限 ÷ 新上限)(再按 0~100 钳制)。</summary>
    private static readonly IReadOnlyDictionary<string, double> OldMax = new Dictionary<string, double>
    {
        ["sharpen"] = 1.00,
        ["clarity"] = 0.50,
        ["usm"] = 1.00,
        ["detail"] = 0.60,
        ["edge"] = 1.00,
    };

    /// <summary>把旧刻度的强度换算成新刻度(保证**同一份设置的实际滤镜强度不变**,即画面不变)。
    /// 例:清晰旧值 50(实际 0.25)= 新值 100(实际 0.25);钝化蒙版旧值 50(实际 0.50)= 新值 100。
    /// 超过新上限的旧值(如钝化蒙版旧 100 = 实际 1.00)会被钳到 100(实际 0.50)—— 这一档本来就是
    /// 实测"会出噪点"的区间,钳制是**有意的**(并在日志里如实记录,不静默)。</summary>
    public static int MigrateStrength(string key, int oldValue)
    {
        if (oldValue <= 0 || !OldMax.TryGetValue(key, out double om) || !SafeMax.TryGetValue(key, out double nm) || nm <= 0)
            return Math.Clamp(oldValue, 0, 100);
        double v = oldValue * (om / nm);
        return Math.Clamp((int)Math.Round(v), 0, 100);
    }

    /// <summary>按强度构造后处理滤镜链(顺序 = 锐化 → 清晰 → 钝化蒙版 → 保留细节 → 边缘增强);全为 0 时返回 null。
    /// 参数范围 0-100,100 = <see cref="SafeMax"/> 里那档的安全上限(实测依据见该类注释)。
    /// 【边缘增强 edgeBoost · 2026-09-15 新增】用户反馈"边缘糊/没对上焦",实测数据(游戏帧,1080p→2x):
    ///   官方 animevideov3 边缘宽度 2.23px / 强边缘对比 52.3 → 加 0.3 档后 2.15px / 58.0(+11%),过冲 1.05%→1.40%;
    ///   其它模型(edge 较弱的那几支)用 0.6 档:2.24→2.16px、对比 56.6→69.3(+22%),过冲 1.23%→1.96%。
    ///   所以界面默认按模型给 0.3 / 0.6(见 VideoView 的按模型推荐值)。
    /// 【与"锐化"的区别】同一族机制(带阈值的 smartblur 反向强度),但**阈值取 8**:只加强明确边缘,
    ///   平坦区/噪点/细碎纹理不动 —— 这就是实测里"边缘变窄而 detail 不掉"的原因;锐化那档阈值是 3/6,作用面更宽。</summary>
    public static string? Build(int sharpen, int clarity, int usm, int detail, int aa = 0, int edgeBoost = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new System.Collections.Generic.List<string>();
        if (sharpen > 0)
            parts.Add($"smartblur=luma_radius=1:luma_strength=-{Strength("sharpen", sharpen).ToString("0.00", inv)}:luma_threshold={(sharpen <= 60 ? 3 : 6)}");
        if (clarity > 0)
            parts.Add($"unsharp=13:13:{Strength("clarity", clarity).ToString("0.00", inv)}:13:13:0");
        if (usm > 0)
            parts.Add($"smartblur=luma_radius=2:luma_strength=-{Strength("usm", usm).ToString("0.00", inv)}:luma_threshold=8");
        if (detail > 0)
            parts.Add($"cas=strength={Strength("detail", detail).ToString("0.00", inv)}");
        // 边缘增强:阈值 8 = 只动明确边缘(实测依据见方法注释)
        if (edgeBoost > 0)
            parts.Add($"smartblur=luma_radius=1:luma_strength=-{Strength("edge", edgeBoost).ToString("0.00", inv)}:luma_threshold=8");
        // 边缘抗锯齿不再产出 ffmpeg 滤镜(见类注释);aa 只保留形参以免调用方签名变化。
        _ = aa;
        return parts.Count > 0 ? string.Join(",", parts) : null;
    }

    /// <summary>滑块值 → 实际强度(线性映射到该档的安全上限)。</summary>
    public static double Strength(string key, int sliderValue)
    {
        if (!SafeMax.TryGetValue(key, out double m)) m = 1.0;
        return Math.Min(m, Math.Max(0, sliderValue) / 100.0 * m);
    }
}
