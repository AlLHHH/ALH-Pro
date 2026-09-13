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
/// diff ≥ 25、强切 ≥ 50、lapvar 比 ≤ 0.6 作为初值(需真机更多样本标定)。</summary>
public static class SceneCutJudge
{
    /// <summary>帧差阈值(mean|diff|,0~255 量级)。实测切点 58.68。【待实测标定】</summary>
    public const double DiffThreshold = 25.0;

    /// <summary>强切阈值:帧差足够大时不必再看拉普拉斯。【待实测标定】</summary>
    public const double StrongDiffThreshold = 50.0;

    /// <summary>拉普拉斯能量下降比例阈值:新帧能量 / 旧帧能量 ≤ 此值视为"画面结构突然变简"(切场典型)。实测 0.474。【待实测标定】</summary>
    public const double LapDropRatio = 0.6;

    /// <summary>逐对判定:第 i 对 = 源帧 i → i+1。<paramref name="lapVar"/> 可为 null(只按帧差判)。
    /// 返回 true = 这一对之间存在**硬切**,插值必须绕开(不做跨切混合)。</summary>
    public static bool IsCut(double meanAbsDiff, double? lapVarPrev, double? lapVarCur)
    {
        if (!double.IsFinite(meanAbsDiff) || meanAbsDiff < DiffThreshold) return false;
        if (meanAbsDiff >= StrongDiffThreshold) return true;
        if (lapVarPrev is > 0 && lapVarCur is >= 0)
            return lapVarCur.Value / lapVarPrev.Value <= LapDropRatio;
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
