namespace AlhPro.Core;

/// <summary>
/// RIFE 补帧的【输出帧号分配】纯逻辑。抽到 Core 是为了能单测钉住——并行补帧时帧号必须
/// 精确连续、不重不漏,否则合帧阶段 ffmpeg 按序读会缺号/乱序 → 整段视频黑帧/花屏。
/// 这是"补帧并行化绝不破坏输出"的基石:并行只是提速,帧号分配必须与串行时完全一致。
/// </summary>
public static class InterpFraming
{
    /// <summary>
    /// 按目标总帧数计算整个序列的补帧布局。
    /// </summary>
    /// <param name="target">目标输出总帧数(=视频时长映射的帧数;串行实现里 Math.Max(1,target))。</param>
    /// <param name="pairs">帧对数量(=源帧数-1)。</param>
    /// <returns>perPairFrames[i] = 第 i 对帧(索引 0..pairs-1)贡献的输出帧数
    /// (1 个左端点帧 + mids_i 个中间帧);totalFrames = 全部输出帧数(不含最后的端帧 files[^1],因为
    /// 串行逻辑里最后一帧是在循环外用 files[^1] 单独复制的,不占 idx 递增)。</returns>
    /// <remarks>
    /// 与串行实现(RifeOnnxInterpDirAsync)逐行对齐:
    ///   mids = Math.Max(0, (target - 1) / pairs - 1);
    ///   若 p &lt; (target - 1) % pairs 则 mids++;
    ///   每对帧输出 = 1(左端点) + mids(中间帧)。
    /// 这里用它推每帧对的帧数,从而算出每帧对在输出序列中的起始帧号(前缀和)。
    /// </remarks>
    public static (int[] perPairFrames, int totalFrames) ComputeLayout(int target, int pairs)
    {
        var perPair = new int[pairs];
        int total = 0;
        for (int p = 0; p < pairs; p++)
        {
            int mids = Math.Max(0, (target - 1) / pairs - 1);
            if (p < (target - 1) % pairs) mids++;
            perPair[p] = 1 + mids;   // 左端点帧 + 中间帧
            total += perPair[p];
        }
        return (perPair, total);
    }

    /// <summary>第 p 个帧对的输出起始帧号(从 1 起)。perPairFrames 来自 <see cref="ComputeLayout"/>。</summary>
    public static int StartIndex(int p, int[] perPairFrames)
    {
        int idx = 1;   // 与串行实现 idx=1 起一致
        for (int i = 0; i < p; i++) idx += perPairFrames[i];
        return idx;
    }
}
