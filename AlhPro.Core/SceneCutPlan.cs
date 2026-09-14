namespace AlhPro.Core;

/// <summary>场景切换(硬切)检测 —— 纯判定,输入是"逐对相邻源帧的指标",不碰图像解码(可单测)。
/// 【任务 S2 · 2026-09-13 用户严格复测推翻旧前提,以下为已实测事实】
/// ① 用户在意的"大跳"**不是 VFR 缺口**,而是源里一次**真实场景硬切**:idx 56→57(t=1.966667),
///    mean|diff| = **58.68**、lapvar **7237→3430**(≈×0.474;画面从"六连击败"结算界面切到游戏场景);
/// ② 两处 66.7ms 缺口**内部几乎没有内容变化**(0.871 / 0.150)→ 缺口里没有运动可填;
/// ③ **均匀化时间轴是有效的**(用户认可:B 消除了 VFR 的 33.3ms 顿挫);
/// ④ **真正的缺陷是"切点鬼影"**:按时轴插值时,落在硬切上的 φ∈(0,1) 合成帧会把前一场景的字迹叠到新场景上
///    (实测鬼影比 A 0.647 / B 0.692),且该处出现**一帧严重软化**(B[57] lapvar 仅源帧的 7.8%)。
/// 【本类的作用】给 S1 的"按时轴逐槽插值"提供**切点清单**,使得**切点上永远不生成 φ∈(0,1) 的混合帧**。
/// 【阈值依据(【待实测标定】)】判据 = `mean|diff| ≥ DiffThreshold` **且** (`拉普拉斯能量下降比例 ≥ LapDropRatio`
/// 或 `mean|diff| ≥ StrongDiffThreshold`):实测量级 mean|diff| 58.68 / lapvar 比 0.474,取
/// diff ≥ 25、强切 ≥ 50、lapvar 比 ≤ 0.6 作为初值(需真机更多样本标定)。
///
/// ==== 【任务 W · 2026-09-14】联网核对"外部怎么做",结论逐条写在这里(出处见 ExternalPractice) ====
/// ① **"切点不插值"确实是业界做法**,而且是**唯一**做法(本项目原先只是推断,现在有逐条出处):
///    · Hybrid(RIFE 的集成方)作者 Selur:**"The scene change detection basically just cuts the scene into
///      chunks, feeds these chunks into RIFE and then adds duplicates around the scene changes to meet the
///      desired frame rate."**(<https://forum.selur.net/thread-3940.html>,帖 #14,第 2 页)—— 即"分段 + 切点补重复帧";
///    · VSGAN-tensorrt-docker(VapourSynth 里做 VFI 的参考工程)的标准写法就是"切点处用**原始帧**替换插值帧":
///      `clip = core.akarin.Select([clip, clip_orig], clip_sc, "x._SceneChangeNext 1 0 ?")`
///      (<https://github.com/styler00dollar/VSGAN-tensorrt-docker>)—— 与我们的
///      <see cref="CutAwareSchedule"/>"切点强制拷贝、绝不合成"**同一条规则**。
/// ② **没有更好的做法**:Selur 明确说 RIFE 本身"blindly interpolating",想改成"预测末帧再插值"只能去改 RIFE 的代码;
///    唯一被提到的替代是"切点做混合(morph)",而那正是**没有切点保护时的默认坏行为**——
///    Selur 的原话:"If you want morphing on scene changes instead, simply disable the scene change detection."
/// ③ **阈值标定参照**(我们 diff≥25 的量级是对的):
///    · PySceneDetect `ContentDetector` 默认 `threshold = 27.0`,度量口径正是"0~255 量级的平均像素变化"
///      (HSV 加权 dHue/dSat/dLum),<https://www.scenedetect.com/docs/latest/api/detectors.html> —— 27 与我们的 25 同量级;
///    · Selur / VSGAN 用的 `misc.SCDetect` 默认 `threshold=0.10`(归一化口径,**不能直接换算到 0~255**)。
/// ④ **已知残差(业界同样没解决)**:切点强制拷贝 = 该处重复一帧,在**摇镜**上会被看成"末帧卡一下"
///    (forum.selur.net thread-3940 里用户的原话抱怨);这是"宁可不插、也不出鬼影"的代价,不是我们的缺陷。
/// ⑤ **【仅建议,未实施】切点最小间距(滞回)**:PySceneDetect 默认 `min_scene_len = 15` 帧
///    (同一出处)—— 我们目前**没有**这条,闪光/频闪素材会连判多个切点 → 连出多次强制拷贝。
///    加它会改变"哪些帧不再被保护"(= 画面语义变化),所以只写建议,见 <see cref="ExternalPractice"/>。</summary>
public static class SceneCutJudge
{
    /// <summary>帧差阈值(mean|diff|,0~255 量级)。实测切点 58.68。【待实测标定】
    /// 【任务 W · 外部标定参照】PySceneDetect `ContentDetector` 默认 `threshold = 27.0`,度量为"0~255 量级的
    /// 平均像素变化(HSV 加权)"(<https://www.scenedetect.com/docs/latest/api/detectors.html>)—— 25 与 27 同量级。
    /// 【不确定度】**口径不完全相同**:PySceneDetect 在**原分辨率**按 HSV 三通道加权算,我们在**192 行灰度采样**上算
    /// (<see cref="SceneCutMetrics.SampleHeight"/>);降采样会同时压低帧差,所以同一个 25 在本工程里**更敏感**
    /// (更容易判切)= 偏保守方向(多出几次"强制拷贝",不会出鬼影)。要精确对齐只能真机复测,未做。</summary>
    public const double DiffThreshold = 25.0;

    /// <summary>强切阈值:帧差足够大时不必再看拉普拉斯。【待实测标定】
    /// 【任务 W】**外部没有对应的"二级阈值"设计**可参照:PySceneDetect 只有一个阈值 + (AdaptiveDetector)
    /// 一条"与滚动均值之比"的自适应判据;Selur/VSGAN 走 `misc.SCDetect` 的单一归一化阈值。
    /// 所以本值纯属本工程口径,**不据外部资料调整**(避免拿"没有出处"的数字改判据)。</summary>
    public const double StrongDiffThreshold = 50.0;

    /// <summary>拉普拉斯能量下降比例阈值:新帧能量 / 旧帧能量 ≤ 此值视为"画面结构突然变简"(切场典型)。实测 0.474。
    /// 【任务 W】**外部没有"用拉普拉斯能量比判切"的做法**(主流是直方图 / HSV 通道差 / 感知哈希 / 神经网络分类器,
    /// 见 <see cref="ExternalPractice"/>);本值只能靠本工程实测,不据外部资料调整。【待实测标定】
    /// 外部对"快摇镜头误判"的解法不是清晰度比,而是**自适应(与局部滚动均值比)**:PySceneDetect
    /// `AdaptiveDetector` 默认 `adaptive_threshold=3.0` / `min_content_val=15.0` / `window_width=2`。
    /// 那条路会改变"哪些对被判为切点" ⇒ 属画面语义变化,只写建议(见 <see cref="ExternalPractice"/>)。</summary>
    public const double LapDropRatio = 0.6;

    /// <summary>逐对判定:第 i 对 = 源帧 i → i+1。<paramref name="lapVar"/> 可为 null(只按帧差判)。
    /// 返回 true = 这一对之间存在**硬切**,插值必须绕开(不做跨切混合)。
    /// 【任务 W】三个阈值改为**经覆盖层读**(<see cref="ParamProfileRuntime"/>):在线参数可覆盖;覆盖层为 null
    /// (默认/离线/全部单测)→ 取上面那三个常量,行为与改动前逐字一致。</summary>
    public static bool IsCut(double meanAbsDiff, double? lapVarPrev, double? lapVarCur)
    {
        double diffThreshold = ParamProfileRuntime.SceneCutDiffThreshold;
        double strongDiffThreshold = ParamProfileRuntime.SceneCutStrongDiffThreshold;
        double lapDropRatio = ParamProfileRuntime.SceneCutLapDropRatio;
        if (!double.IsFinite(meanAbsDiff) || meanAbsDiff < diffThreshold) return false;
        if (meanAbsDiff >= strongDiffThreshold) return true;
        if (lapVarPrev is > 0 && lapVarCur is >= 0)
            return lapVarCur.Value / lapVarPrev.Value <= lapDropRatio;
        return true;   // 帧差已过阈值但拿不到拉普拉斯 → 保守判为切点(宁可少插一帧,也不出鬼影)
    }

    /// <summary>批量检测:返回"硬切发生在源帧 i → i+1 之间"的 i 列表(升序、去重)。
    /// 采样少于 2 帧 → 空(不判切,保持既有行为)。</summary>
    public static IReadOnlyList<int> Detect(IReadOnlyList<double> meanAbsDiff, IReadOnlyList<double>? lapVar = null)
    {
        var cuts = new List<int>();
        int n = meanAbsDiff?.Count ?? 0;
        for (int i = 0; i < n; i++)
        {
            double? lp = lapVar != null && i < lapVar.Count ? lapVar[i] : null;
            double? lc = lapVar != null && i + 1 < lapVar.Count ? lapVar[i + 1] : null;
            if (IsCut(meanAbsDiff![i], lp, lc)) cuts.Add(i);
        }
        return cuts;
    }
}

/// <summary>「切点对齐」的排程(纯逻辑,可单测)—— S2 的核心:**切点上不许出现跨场景混合帧**。
/// 规则(对每个目标时刻 t,已知它落在源帧 idx0 → idx1 之间、插值系数 φ):
/// · 若 idx0 → idx1 **不是切点** → 照常返回 φ(交调用方按 `-s φ` 合成);
/// · 若是切点 → **不生成混合帧**:t 落在切点前 → 返回"拷贝 idx0"(φ=0);t 落在切点后 → "拷贝 idx1"(φ=1);
///   切点时刻自身(±半帧内)→ 按"新场景开始"处理(拷贝 idx1)。
/// 目标:输出不再有跨切点鬼影帧,也不再出现"切点上一帧严重软化"(实测 B[57] lapvar 仅源帧 7.8%)。</summary>
public static class CutAwareSchedule
{
    /// <summary>排程结果:要么"直接拷贝第 <paramref name="CopyIndex"/> 帧",要么"在 idx0/idx1 之间按 <paramref name="Phi"/> 合成"。</summary>
    public readonly record struct Slot(bool Copy, int CopyIndex, int Idx0, int Idx1, double Phi, bool AtCut);

    /// <summary>排一个目标槽。<paramref name="cuts"/> = <see cref="SceneCutJudge.Detect"/> 的结果(升序)。
    /// <paramref name="exactSource"/> = t 正好落在某张源帧上(调用方已由 PTS 判定)→ 一律拷贝,与切点无关。</summary>
    public static Slot Plan(int idx0, int idx1, double phi, bool exactSource, IReadOnlyCollection<int>? cuts)
    {
        bool isCut = cuts != null && idx1 == idx0 + 1 && cuts.Contains(idx0);
        if (exactSource || phi <= 0)
            return new Slot(true, idx0, idx0, idx1, 0, isCut);
        if (phi >= 1)
            return new Slot(true, idx1, idx0, idx1, 1, isCut);
        if (isCut)
        {
            // 切点:不许混合。φ < 0.5 视作仍属前一场景(拷前一帧),否则属新场景(拷新帧)。
            bool beforeCut = phi < 0.5;
            return new Slot(true, beforeCut ? idx0 : idx1, idx0, idx1, beforeCut ? 0 : 1, true);
        }
        return new Slot(false, -1, idx0, idx1, phi, false);
    }

    /// <summary>整条时间轴的排程:返回每个目标槽的结果,并统计"因切点被强制拷贝"的槽数。
    /// 用 <see cref="TimelineFlattenPlan.MapTargetFrame"/> 做时轴映射,再按切点改写 —— 两步都是纯函数。</summary>
    public static IReadOnlyList<Slot> PlanAll(IReadOnlyList<double> frameDurs, int targetFrames, double targetFps,
        IReadOnlyCollection<int>? cuts, out int forcedCopies)
    {
        forcedCopies = 0;
        var list = new List<Slot>(Math.Max(0, targetFrames));
        for (int k = 0; k < targetFrames; k++)
        {
            if (!TimelineFlattenPlan.MapTargetFrame(frameDurs, k, targetFps, out int i0, out int i1, out double phi, out bool exact))
                continue;
            var slot = Plan(i0, i1, phi, exact, cuts);
            if (slot.AtCut && slot.Copy && !exact) forcedCopies++;
            list.Add(slot);
        }
        return list;
    }
}
