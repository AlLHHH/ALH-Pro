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

    /// <summary>该模型**原生权重**的倍率提示文字(给界面的悬停提示末尾那行绿字用,纯逻辑)。
    ///
    /// 【用户要求(2026-09-25)】「**在每一个模型的悬停提示里面最后加一个绿字提醒「权重 2x」这样子**」。
    /// 背景:各支超分模型的原生权重倍率并不一样 —— animevideov3 有 2x/3x/4x、Real-CUGAN models-se 有
    /// 2x/3x/4x、x4plus 系只有 **4x**、waifu2x 三支都是 **2x**;倍率与权重不一致时
    /// 上层会用重采样补齐(**那不是超分**)。这件事原先只散写在部分提示里,现在统一成一行绿字。
    ///
    /// 【判据复用】这里的分类**完全复用 <see cref="Decide"/> 里那些谓词**
    /// (<see cref="ExperimentalEsrgan.Is1xModel"/> / <see cref="Is4xOnlyModel"/>),
    /// 不新造第二套 —— 否则"提示说 4x、实际按 2x 跑"这种自相矛盾迟早出现。
    ///
    /// 【返回值】空串 = 认不出/不适用(调用方据此**不加**绿字行):
    ///   · Real-CUGAN → `权重 2x / 3x / 4x`
    ///   · realesrgan + 1x 修复模型 → `权重 2x(缩回 1x)`
    ///   · realesrgan + 4x-only(x4plus 系 / general-x4v3 / wdn-x4v3)→ `权重 4x`
    ///   · realesrgan + animevideov3 → `权重 2x / 3x / 4x`
    ///   · waifu2x + cunet / upconv_7_photo / upconv_7_anime_style_art_rgb → `权重 2x`
    ///   · 其它(含 Anime4K —— 它走着色器、根本没有权重文件)→ `""`
    /// 【注】界面上的「动漫 · Anime4K 修复」那一项自己写死一行绿字 `着色器(无权重)`,因为它不是引擎权重模型
    /// (Tag 是 `anime4k`),不该硬蹭这里的判据。(2026-09-27 起 1x 档只剩这一条;原先「现实 · 1x 修复」那条
    /// 写死 `权重 2x(缩回 1x)`,它已下线。)</summary>
    public static string NativeWeightLabel(string engine, string model)
    {
        string m = model ?? "";
        // Real-CUGAN:models-se 三档都同时具备 up2x/up3x/up4x(与 Decide 的注释同源)。
        if (string.Equals(engine, RealCugan.EngineName, StringComparison.OrdinalIgnoreCase))
            return "权重 2x / 3x / 4x";
        // waifu2x:随包三支的权重都是 2x(upconv_7_photo / upconv_7_anime_style_art_rgb 只有 noiseN_scale2.0x)。
        if (string.Equals(engine, "waifu2x", StringComparison.OrdinalIgnoreCase))
        {
            if (m.Contains("cunet", StringComparison.OrdinalIgnoreCase)
                || m.Contains("upconv_7_photo", StringComparison.OrdinalIgnoreCase)
                || m.Contains("upconv_7_anime_style_art_rgb", StringComparison.OrdinalIgnoreCase))
                return "权重 2x";
            return "";
        }
        if (!string.Equals(engine, "realesrgan", StringComparison.OrdinalIgnoreCase))
            return "";   // 其它引擎(Anime4K 走着色器、没有权重)与认不出的名字 ⇒ 调用方不加绿字行
        // Real-ESRGAN:判据与 Decide 逐条同一套谓词(顺序也一致:先 1x、再 4x-only)。
        if (ExperimentalEsrgan.Is1xModel(m)) return "权重 2x(缩回 1x)";
        if (Is4xOnlyModel(m)) return "权重 4x";
        // animevideov3:名字口径与 PipelineOrderPlan.NormalizeModel 同一处(它有原生的 x2/x3/x4 三套权重)。
        if (PipelineOrderPlan.NormalizeModel(m) == "animevideov3") return "权重 2x / 3x / 4x";
        return "";
    }

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
    /// 【Real-CUGAN · 2026-09-24 新增】**也绝不返回 1**:随包的 models-se 三档只有 up2x/up3x/up4x,
    /// 没有任何 1x 权重 ⇒ 目标 ≤1x 一律走"2x 放大后缩回"(与 Real-ESRGAN 的 1x 护栏同一手法);
    /// 2/3/4 按目标直接下发(三档都有对应权重,不需要像 x4plus 那样固定 4x)。
    /// 【Real-ESRGAN】**绝不返回 1**:x4plus 系固定 4;
    /// 其余(animevideov3)按 ceil 到 2/3/4,目标 ≤1 时改走 2x 再缩回(缺 x1 权重会静默全黑,见类注释)。</summary>
    public static Decision Decide(string engine, string model, double requestedScale)
    {
        double want = requestedScale > 0 && double.IsFinite(requestedScale) ? requestedScale : 1.0;
        // 【Real-CUGAN】原生权重档 = 2 / 3 / 4(models-se 三档都有这三套);目标 ≤1x 走 2x 再缩回。
        if (string.Equals(engine, RealCugan.EngineName, StringComparison.OrdinalIgnoreCase))
        {
            int rc = want <= 2.0 ? 2 : want <= 3.0 ? 3 : 4;
            return want < 2.0
                ? new Decision(rc, want / rc,
                    "Real-CUGAN 的权重只有 2x/3x/4x(没有 1x):改用 2x 放大后缩回目标尺寸")
                : new Decision(rc, want / rc, "");
        }
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
        // 【2026-10-14 删了一段】原先这里还有一条"只有 2x 原生权重的**自训**模型(游戏向/现实向)固定 2"的分支:
        // 那几支权重已按用户要求删除(见 ExperimentalEsrgan 类注释),判据 IsX2OnlyModel 随之作废 ——
        // 现在官方 realesrgan 里没有"只有 2x 权重"的模型(animevideov3 有原生 x2/x3/x4 三套)。
        // 历史实测(下发 -s 3/-s 4 = 镜像平铺的错帧、exit=0 不报错)记在 git 历史里,不再留在代码中。
        int eng = Math.Clamp((int)Math.Ceiling(want), 1, 4);
        if (eng <= 1)
            return new Decision(2, want / 2.0,
                "该模型没有 1x 权重(-s 1 会静默输出全黑帧,exit=0 不报错):改用 2x 放大后缩回目标尺寸");
        return new Decision(eng, want / eng, "");
    }
}
