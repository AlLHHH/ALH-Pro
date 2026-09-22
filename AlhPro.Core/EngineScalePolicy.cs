namespace AlhPro.Core;

/// <summary>超分引擎"实际下发倍数"的策略(纯逻辑,可单测)。
/// 【任务 O1 · 2026-09-13 真机基准发现:Real-ESRGAN 用 1x 会静默输出全黑帧】
/// 实测:`realesr-animevideov3` 传 `-s 1` → 输出**纯黑**(mean=0.00 / std=0.00 / uniq=1,两张不同帧复现),
/// **exit=0 无报错**,引擎 stdout 只有 `_wfopen models/realesr-animevideov3-x1.param failed / network graph not ready`
/// —— 模型目录里根本没有 x1 权重(实测目录只有 x2/x3/x4 三套 + general-x4v3/x4plus/x4plus-anime)。
/// 也就是说:引擎**不会**因为权重缺失而失败退出,而是"画一张全黑给你、还报成功"。
/// 【为什么必须有这条策略】waifu2x 路径早在 `EngineService.UpscaleImageAsync/UpscaleDirAsync` 里就有 1x 护栏
/// (不降噪直接复制 / 降噪改 2x 再缩回),**Real-ESRGAN 路径漏了**:
/// 批量路径原实现是 `Math.Clamp((int)Math.Ceiling(scale), 1, 4)`,scale ≤ 1 时正好落到 1 → 直接 `-s 1`。
/// 现在把"引擎倍数"的判定收成唯一来源:任何 Real-ESRGAN 判定都**不会**返回 1,而是走"2x 后缩回"这条现有路径
/// (与 1x 缩回同一手法:`ShrinkRatio = 目标/引擎倍数`,调用方已有的缩放步骤直接生效)。</summary>
public static class EngineScalePolicy
{
    /// <summary>x4plus 系(含自转的 general-x4v3、带降噪的 general-wdn-x4v3)只有 4x 权重:
    /// 必须按原生 4x 跑再缩回。
    /// 【2026-09-14 补 wdn】自转的 `realesr-general-wdn-x4v3`(官方 BSD-3 权重 pth→ncnn)与 general-x4v3 同架构、
    /// 同样只有 4x 权重。**不在这里登记会出事**:它既不含 "x4plus" 也不含 "general-x4v3"(名字是
    /// general-**wdn**-x4v3),会被当成普通模型 → 目标 2x 时下发 `-s 2`。实测该路径下输出与双三次的
    /// PSNR 只有 ~14 dB(官方 general-x4v3 走 `-s 2` 同样 13.92 dB),即"用 4x 权重按 2x 贴图"的坏路径;
    /// 登记后固定走 4x + 缩回,与既有 x4plus 家族一致。</summary>
    public static bool Is4xOnlyModel(string model)
    {
        string m = model ?? "";
        return m.Contains("x4plus", StringComparison.OrdinalIgnoreCase)
            || m.Contains("general-x4v3", StringComparison.OrdinalIgnoreCase)
            || m.Contains("wdn-x4v3", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>该模型有没有原生 1x 权重。
    /// 【2026-09-20 变更】原来恒为 false(Real-ESRGAN 全家都只有 x2/x3/x4 权重,传 `-s 1` 会**静默输出全黑帧**)。
    /// 现在多了一种:**1x 修复模型**(同尺寸:去压缩痕 + 边缘恢复,见 <see cref="ExperimentalEsrgan.Is1xModel"/>)——
    /// 它的网络尾层就是 3 通道、倍率写死 1 ⇒ `-s 1` 是它的**原生**用法,不是护栏,也不会有全黑问题
    /// (⚠ 上线前必须真机跑一次 `-s 1` 确认非黑、尺寸不变 —— 这是本仓库对"静默全黑"的固定验收动作)。</summary>
    public static bool ModelHasNative1x(string engine, string model)
        => ExperimentalEsrgan.Is1xModel(model);

    /// <summary>**只有 2x 原生权重**的模型 = 两支自训模型(游戏向 / 现实向,见 <see cref="ExperimentalEsrgan"/>)。
    /// 【为什么必须登记】2026-09-15 真机实测(本机 4060 Laptop,220×220 输入,`-t 0`,`-m models -n alhpro-real2x`):
    ///   · `-s 2` → 440×440、正常(mean 126.43/max 254);
    ///   · `-s 3` → 660×660、`-s 4` → 880×880,**两份都是 exit=0、没有任何报错**,
    ///     但 `-s 4` 的成图是**镜像平铺的错帧**(画面被切成 2×2 的镜像重复,下半幅还整体偏黄),
    ///     与"2x 成图再双三次放大"的 PSNR 只有 7.27 dB —— 即引擎按目标倍数贴回分块、而网络实际只放大 2x,
    ///     于是把 2x 的内容当目标倍数铺了出去。**画面明显错、却不报错**,这正是本仓库最怕的那类静默故障。
    ///   ⇒ 所以这两支必须固定按原生 2x 跑,再由上层缩放到目标尺寸(与 x4plus 系走"原生 4x + 缩回"同一手法)。
    /// 【与 Is4xOnlyModel 的关系】两支自训模型的名字既不含 "x4plus" 也不含 "general-x4v3",
    /// 若不在这里登记就会被当成"普通模型"→ 目标 4x 时下发 `-s 4` → 错帧。两个判定互斥(2x / 4x)。</summary>
    public static bool IsX2OnlyModel(string model) => ExperimentalEsrgan.IsX2Only(model);

    /// <summary>引擎倍数决策结果。<paramref name="ShrinkRatio"/> = 目标倍数 ÷ 引擎倍数
    /// (≠1 表示调用方需要把引擎输出缩放到目标尺寸,现有代码就是 `scale / engineScale`)。
    /// <paramref name="Reason"/> 非空 = 发生了"护栏改写",调用方应当记日志(用户要能看懂为什么改了倍数)。</summary>
    public readonly record struct Decision(int EngineScale, double ShrinkRatio, string Reason)
    {
        public bool NeedsShrinkBack => Math.Abs(ShrinkRatio - 1.0) > 1e-6;
    }

    /// <summary>按引擎/模型/目标倍数给出"实际下发给引擎的倍数"。
    /// 【waifu2x】沿用原口径:取不小于目标的最大 2 的幂(`CeilPowerOfTwo`),它的 1x 特例(不降噪复制 /
    /// 降噪改 2x)依赖 `noise`,由调用方处理 —— 本函数不改变其行为。
    /// 【Real-ESRGAN】**绝不返回 1**:x4plus 系固定 4;只有 2x 权重的自训模型(游戏向/现实向)固定 2;
    /// 其余(animevideov3)按 ceil 到 2/3/4,目标 ≤1 时改走 2x 再缩回(缺 x1 权重会静默全黑,见类注释)。</summary>
    public static Decision Decide(string engine, string model, double requestedScale)
    {
        double want = requestedScale > 0 && double.IsFinite(requestedScale) ? requestedScale : 1.0;
        if (!string.Equals(engine, "realesrgan", StringComparison.OrdinalIgnoreCase))
            return new Decision(PathUtil.CeilPowerOfTwo(want), 1.0, "");
        // 【1x 修复模型】倍率写死 1(同尺寸):1x 目标是它的原生用法 ⇒ 引擎倍数 1、不缩放。
        // 【为什么可以下发 -s 1】以前禁止 -s 1 是因为**没有 x1 权重**的模型会静默输出全黑帧;
        // 这一支的网络尾层就是 3 通道、倍率 1 ⇒ -s 1 正常(真机验收见 ModelHasNative1x 的注释)。
        if (ExperimentalEsrgan.Is1xModel(model))
        {
            if (Math.Abs(want - 1.0) < 1e-6)
                return new Decision(1, 1.0, "");
            return new Decision(1, want,
                $"该模型是「1x 修复(同尺寸)」模型,不能放大:目标 {want:0.##}x 请改用对应倍率的超分模型(否则只会普通放大,没有超分效果)");
        }
        if (Is4xOnlyModel(model))
            return new Decision(4, want / 4.0, "");
        // 【只有 2x 权重】固定原生 2x,再缩回目标(下发 -s 3/-s 4 会得到镜像平铺的错帧、且 exit=0 不报错)。
        // 目标正好 2x = 正常路径,不留理由(不刷日志);其余目标才给理由,让用户看得懂"为什么改了倍数"。
        if (IsX2OnlyModel(model))
        {
            if (Math.Abs(want - 2.0) < 1e-6)
                return new Decision(2, 1.0, "");
            // 【措辞只写实测到的事】>2x 那档实测过(3x/4x = 镜像平铺错帧);<2x 那档没测,
            // 只陈述"没有 1x 权重"这一事实,不搬 x4plus 那些"全黑"的实测数字来吓人。
            // 【2026-09-20 1x 目标加一句实话】实测:2x 超分再缩回 1080p,detail 822、边宽 5.86px,
            // **比原始 1080p(detail 1152 / 7.02px)还软** ⇒ 1x 目标下这条路径只会让画面更糊,
            // 必须把结论写进理由里,并给出可执行的两条出路(换 1x 修复模型 / 把目标改成 2x)。
            string why = want > 2.0
                ? $"该模型只有 2x 原生权重(实测下发 -s {(int)want} 会输出镜像平铺的错帧、且 exit=0 不报错)"
                : "该模型只有 2x 原生权重(没有 x1 权重)";
            string tail = Math.Abs(want - 1.0) < 1e-6
                ? ":改用 2x 超分后再缩放到目标尺寸(⚠ 实测这条路径比原片更软:detail 822 对原片 1152)—— 1x 想要更清晰请改用「1x 修复(同尺寸)」模型,或把目标改为 2x"
                : ":改用 2x 超分后再缩放到目标尺寸";
            return new Decision(2, want / 2.0, why + tail);
        }
        int eng = Math.Clamp((int)Math.Ceiling(want), 1, 4);
        if (eng <= 1)
            return new Decision(2, want / 2.0,
                "该模型没有 1x 权重(-s 1 会静默输出全黑帧,exit=0 不报错):改用 2x 放大后缩回目标尺寸");
        return new Decision(eng, want / eng, "");
    }
}
