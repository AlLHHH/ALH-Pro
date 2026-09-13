namespace AlhPro.Core;

/// <summary>「边缘抗锯齿(edge smooth)」的像素级实现 —— 从 EngineService.ApplyEdgeSmoothInMemory 抽出的纯逻辑(可单测)。
/// 【语义逐字保持】改这里 = 改所有开了这一档的成片(视频页 4K/4320p 每帧都要跑),所以口径不许动:
///   · 边缘判定:中心像素与 3×3 邻域(越界按 clamp 复制边界)的【最大绝对差】≥ 16 才算边缘,平坦区一律不改;
///   · 边缘像素:out = 原值 + (3×3 均值 − 原值) × mix,mix = 强度/100 × 0.55;
///   · 均值 = 9 个样本之和【整数除 9】;四舍五入用 Math.Round(银行家舍入,与旧实现一致);最后 clamp 到 0~255。
/// 【为什么要抽出来】① 这是"改错就成片画质漂移"的逻辑,必须有单测钉住;
/// ② 它是视频页最热的一步(4K 单帧逐像素跑三遍),纯函数才能单独测边界列/平坦区/舍入这些最容易写错的角落。</summary>
public static class EdgeSmooth
{
    /// <summary>中心与邻域的最大绝对差 ≥ 该值才算边缘(与旧实现同一个 16:改了画面就变,不要动)。</summary>
    public const int EdgeThreshold = 16;

    /// <summary>强度(0~100,与界面滑条同口径)→ 混合系数。0.55 是旧实现的上限,不要动。</summary>
    public static double MixFor(int strength) => strength / 100.0 * 0.55;

    /// <summary>对单通道平面做一次边缘平滑(就地写回 src;邻域读取用内部副本,故写入不会影响邻居)。
    /// 【等价重写(本次提速)】旧实现对每个像素做 9 次 Math.Clamp 求邻域坐标 + 9 次索引乘加;
    /// 现在把"行列是否越界"提到循环外:内部像素(x=1..w-2)的左右邻居【必然】在界内,
    /// 直接取 x-1/x+1 即可 —— 与 clamp 的结果完全一致;边界行/列仍按 clamp 语义单独处理。
    /// 逐字节结果与旧实现相同(有单测与朴素实现比对:各种尺寸/强度/边界)。
    /// 【未真机实测】提速幅度未在真机空闲时测过(占显卡/跑基准被明令禁止),这里只声明"等价 + 少若干次 clamp/索引"。</summary>
    public static void Channel(byte[] src, int w, int h, int strength)
    {
        if (src == null || w <= 0 || h <= 0) return;
        if ((long)w * h > src.Length) return;      // 尺寸与缓冲不匹配:调用方 bug,这里不猜、不动
        double mix = MixFor(strength);
        if (mix <= 0) return;
        var orig = new byte[src.Length];
        Buffer.BlockCopy(src, 0, orig, 0, src.Length);
        for (int y = 0; y < h; y++)
        {
            // 邻域三行的行基址:越界行按 clamp 复制首行/末行(与逐像素 clamp(y+dy) 完全一致)
            int rUp = (y > 0 ? y - 1 : 0) * w;
            int rMid = y * w;
            int rDn = (y < h - 1 ? y + 1 : y) * w;
            // 左边界像素 x=0:左邻居越界 → clamp 到自己
            Pixel(orig, src, w, 0, 0, w > 1 ? 1 : 0, rUp, rMid, rDn, mix);
            // 内部像素:左右邻居必然在界内 → 无 clamp、无分支(热路径)
            for (int x = 1; x < w - 1; x++)
            {
                int center = orig[rMid + x];
                int v0 = orig[rUp + x - 1], v1 = orig[rUp + x], v2 = orig[rUp + x + 1];
                int v3 = orig[rMid + x - 1], v5 = orig[rMid + x + 1];
                int v6 = orig[rDn + x - 1], v7 = orig[rDn + x], v8 = orig[rDn + x + 1];
                int sum = v0 + v1 + v2 + v3 + center + v5 + v6 + v7 + v8;
                // 最大绝对差 = max(|v−center|) = max(max(v)−center, center−min(v)):极值必在 max/min 两处取到
                int mn = Math.Min(Math.Min(Math.Min(v0, v1), Math.Min(v2, v3)),
                                  Math.Min(Math.Min(v5, v6), Math.Min(v7, v8)));
                int mx = Math.Max(Math.Max(Math.Max(v0, v1), Math.Max(v2, v3)),
                                  Math.Max(Math.Max(v5, v6), Math.Max(v7, v8)));
                mn = Math.Min(mn, center);
                mx = Math.Max(mx, center);
                if (mx - center < EdgeThreshold && center - mn < EdgeThreshold) continue;   // 平坦区:不动
                src[rMid + x] = Mix(center, sum, mix);
            }
            // 右边界像素 x=w-1:右邻居越界 → clamp 到自己(w==1 时与左边界是同一个像素,只处理一次)
            if (w >= 2) Pixel(orig, src, w, w - 1, w - 2, w - 1, rUp, rMid, rDn, mix);
        }
    }

    /// <summary>边界像素(左/右列):邻域列由调用方按 clamp 语义给出。</summary>
    private static void Pixel(byte[] orig, byte[] src, int w, int x, int xl, int xr,
        int rUp, int rMid, int rDn, double mix)
    {
        int center = orig[rMid + x];
        int v0 = orig[rUp + xl], v1 = orig[rUp + x], v2 = orig[rUp + xr];
        int v3 = orig[rMid + xl], v5 = orig[rMid + xr];
        int v6 = orig[rDn + xl], v7 = orig[rDn + x], v8 = orig[rDn + xr];
        int sum = v0 + v1 + v2 + v3 + center + v5 + v6 + v7 + v8;
        int mn = Math.Min(Math.Min(Math.Min(v0, v1), Math.Min(v2, v3)),
                          Math.Min(Math.Min(v5, v6), Math.Min(v7, v8)));
        int mx = Math.Max(Math.Max(Math.Max(v0, v1), Math.Max(v2, v3)),
                          Math.Max(Math.Max(v5, v6), Math.Max(v7, v8)));
        mn = Math.Min(mn, center);
        mx = Math.Max(mx, center);
        if (mx - center < EdgeThreshold && center - mn < EdgeThreshold) return;
        src[rMid + x] = Mix(center, sum, mix);
    }

    /// <summary>边缘像素的新值:原值 + (均值 − 原值) × mix;均值 = 总和 ÷ 9(整数除法,与旧实现一致)。</summary>
    private static byte Mix(int center, int sum, double mix)
    {
        int mean = sum / 9;
        int outV = center + (int)Math.Round((mean - center) * mix);
        return (byte)Math.Clamp(outV, 0, 255);
    }
}
