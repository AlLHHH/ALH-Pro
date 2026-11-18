// 实验2:验证"边缘封闭性"。Canny 边缘覆盖率仅13% → 泛洪从缺口溢出。
// 方案对比:(A) Canny滞后  (B) 全部弱边阻断  (C) Canny+膨胀  (D) 相邻像素Lab差阻断
using System;
using System.Collections.Generic;

int W = 400, H = 400;
int cx0 = 200, cy0 = 200, R = 120;

void Run(string name, int circleGray, double textureAmp)
{
    var px = new byte[W * H * 4];
    var rnd = new Random(42);
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int dx = x - cx0, dy = y - cy0;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int i = (y * W + x) * 4;
            if (dist < R)
            {
                double f = 1.0 - 0.10 * (dist / R);
                double t = textureAmp > 0 ? (rnd.NextDouble() * 2 - 1) * textureAmp : 0;
                int v = (int)(circleGray * f + t);
                px[i] = (byte)v; px[i + 1] = (byte)v; px[i + 2] = (byte)v;
            }
            else { px[i] = 245; px[i + 1] = 245; px[i + 2] = 245; }
        }
    double[] l = new double[W * H], a = new double[W * H], bb = new double[W * H];
    for (int i = 0; i < W * H; i++)
        RgbToLab(px[i * 4 + 2], px[i * 4 + 1], px[i * 4], out l[i], out a[i], out bb[i]);

    Console.WriteLine($"--- {name} (圆灰度={circleGray}, 纹理={textureAmp}) ---");

    // 泛洪 + 各阻断方案,统计圆内覆盖率 与 圆外溢出率
    foreach (var scheme in new[] { "A:canny220", "B:allWeak110", "C:canny+dilate", "D:adjacentLab", "E:adjacentLab+canny" })
    {
        bool[] edge = scheme switch
        {
            "A:canny220" => Canny(px, W, H, 220, 110, false),
            "B:allWeak110" => Canny(px, W, H, 220, 110, true),
            "C:canny+dilate" => Dilate(Canny(px, W, H, 220, 110, false), W, H),
            "D:adjacentLab" => new bool[W * H],
            _ => Canny(px, W, H, 220, 110, false),
        };
        bool useAdj = scheme is "D:adjacentLab" or "E:adjacentLab+canny";
        double adjThrSq = 12.0 * 12.0;

        // 泛洪:圆心种子
        var want = new byte[W * H];
        var orderArr = new int[W * H];
        Array.Fill(orderArr, -1);
        double brushR = 6, r2 = brushR * brushR;
        double thr = 8.0 + 20 / 100.0 * 32.0, thrSq = thr * thr;
        int ord = 0, wv = 1;
        var queue = new Queue<(int X, int Y, double rl, double ra, double rb, int want, int order)>();
        var seed = (cx0, cy0);
        double rl2 = 0, ra2 = 0, rb2 = 0; int refN = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int y = seed.Item2 + dy, x = seed.Item1 + dx;
                if (y < 0 || y >= H || x < 0 || x >= W) continue;
                int ii = y * W + x;
                rl2 += l[ii]; ra2 += a[ii]; rb2 += bb[ii]; refN++;
            }
        rl2 /= refN; ra2 /= refN; rb2 /= refN;
        for (int dy = -(int)Math.Ceiling(brushR); dy <= (int)Math.Ceiling(brushR); dy++)
        {
            int y = seed.Item2 + dy;
            if (y < 0 || y >= H) continue;
            for (int dx = -(int)Math.Ceiling(brushR); dx <= (int)Math.Ceiling(brushR); dx++)
            {
                int x = seed.Item1 + dx;
                if (x < 0 || x >= W) continue;
                if (dx * dx + dy * dy > r2) continue;
                int ii = y * W + x;
                if (orderArr[ii] < ord) { want[ii] = (byte)wv; orderArr[ii] = ord; queue.Enqueue((x, y, rl2, ra2, rb2, wv, ord)); }
            }
        }
        while (queue.Count > 0)
        {
            var (qx, qy, ql, qa, qb, w2, o2) = queue.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                int nx = qx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int ny = qy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int nidx = ny * W + nx;
                if (edge[nidx]) continue;
                if (useAdj)
                {
                    int cidx = qy * W + qx;
                    double dl = l[nidx] - l[cidx], da = a[nidx] - a[cidx], db = bb[nidx] - bb[cidx];
                    if (dl * dl + da * da + db * db > adjThrSq) continue;   // 相邻像素突变=边界
                }
                if (orderArr[nidx] > o2) continue;
                if (orderArr[nidx] == o2 && want[nidx] == w2) continue;
                double dl2 = l[nidx] - ql, da2 = a[nidx] - qa, db2 = bb[nidx] - qb;
                double de2 = dl2 * dl2 + da2 * da2 + db2 * db2;
                if (de2 > thrSq) continue;
                want[nidx] = (byte)w2;
                orderArr[nidx] = o2;
                queue.Enqueue((nx, ny, ql, qa, qb, w2, o2));
            }
        }
        int inC = 0, outC = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (want[y * W + x] != 1) continue;
                double dist = Math.Sqrt((x - cx0) * (x - cx0) + (y - cy0) * (y - cy0));
                if (dist < R) inC++; else outC++;
            }
        int area = (int)(Math.PI * R * R);
        Console.WriteLine($"  {scheme,-16} 圆内覆盖率={100.0 * inC / area,5:F1}%  圆外溢出={outC}px");
    }
}

Run("强反差边界 100", 145, 0);
Run("中反差边界 60", 185, 0);
Run("弱反差边界 35", 210, 0);
Run("纹理内 反差60+纹理±15", 185, 15);

static bool[] Dilate(bool[] e, int w, int h)
{
    var o = new bool[e.Length];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!e[y * w + x]) continue;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    o[ny * w + nx] = true;
                }
        }
    return o;
}

static bool[] Canny(byte[] px, int w, int h, float hi, float lo, bool allWeak)
{
    int n = w * h;
    var gray = new float[n];
    for (int i = 0; i < n; i++)
        gray[i] = 0.299f * px[i * 4 + 2] + 0.587f * px[i * 4 + 1] + 0.114f * px[i * 4];
    var blur = new float[n];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float s = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1), xx = Math.Clamp(x + dx, 0, w - 1);
                    double wgt = dx == 0 && dy == 0 ? 4 : dx == 0 || dy == 0 ? 2 : 1;
                    s += (float)(gray[yy * w + xx] * wgt);
                }
            blur[y * w + x] = s / 16f;
        }
    var grad = new float[n];
    var angle = new float[n];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int xm = Math.Clamp(x - 1, 0, w - 1), xp = Math.Clamp(x + 1, 0, w - 1);
            int ym = Math.Clamp(y - 1, 0, h - 1), yp = Math.Clamp(y + 1, 0, h - 1);
            float tl = blur[ym * w + xm], t = blur[ym * w + x], tr = blur[ym * w + xp];
            float ml = blur[y * w + xm], mr = blur[y * w + xp];
            float bl = blur[yp * w + xm], b = blur[yp * w + x], br = blur[yp * w + xp];
            float gx = (tr + 2 * mr + br) - (tl + 2 * ml + bl);
            float gy = (bl + 2 * b + br) - (tl + 2 * t + tr);
            grad[y * w + x] = (float)Math.Sqrt(gx * gx + gy * gy);
            angle[y * w + x] = (float)Math.Atan2(gy, gx);
        }
    var nms = new float[n];
    for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int idx = y * w + x;
            float ang = angle[idx];
            double d = Math.Abs(ang) * 180.0 / Math.PI;
            int q = (int)Math.Round(d / 45.0) % 4;
            float g1 = 0, g2 = 0;
            if (q == 0) { g1 = grad[y * w + x + 1]; g2 = grad[y * w + x - 1]; }
            else if (q == 1) { g1 = grad[(y + 1) * w + x + 1]; g2 = grad[(y - 1) * w + x - 1]; }
            else if (q == 2) { g1 = grad[(y + 1) * w + x]; g2 = grad[(y - 1) * w + x]; }
            else { g1 = grad[(y + 1) * w + x - 1]; g2 = grad[(y - 1) * w + x + 1]; }
            nms[idx] = grad[idx] >= g1 && grad[idx] >= g2 ? grad[idx] : 0;
        }
    var edge = new bool[n];
    if (allWeak)
    {
        for (int i = 0; i < n; i++)
            if (nms[i] >= lo) edge[i] = true;
        return edge;
    }
    var strong = new bool[n]; var weak = new bool[n];
    for (int i = 0; i < n; i++)
    {
        if (nms[i] >= hi) strong[i] = true;
        else if (nms[i] >= lo) weak[i] = true;
    }
    var stack = new Stack<int>();
    for (int i = 0; i < n; i++) if (strong[i]) { edge[i] = true; stack.Push(i); }
    while (stack.Count > 0)
    {
        int idx = stack.Pop();
        int cx = idx % w, cy = idx / w;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (!edge[nidx] && weak[nidx]) { edge[nidx] = true; stack.Push(nidx); }
            }
    }
    return edge;
}

static void RgbToLab(int R, int G, int B, out double L, out double A, out double oB)
{
    double fr = R / 255.0, fg = G / 255.0, fb = B / 255.0;
    fr = fr <= 0.04045 ? fr / 12.92 : Math.Pow((fr + 0.055) / 1.055, 2.4);
    fg = fg <= 0.04045 ? fg / 12.92 : Math.Pow((fg + 0.055) / 1.055, 2.4);
    fb = fb <= 0.04045 ? fb / 12.92 : Math.Pow((fb + 0.055) / 1.055, 2.4);
    double X = (fr * 0.4124564 + fg * 0.3575761 + fb * 0.1804375) / 0.95047;
    double Y = (fr * 0.2126729 + fg * 0.7151522 + fb * 0.0721750) / 1.00000;
    double Z = (fr * 0.0193339 + fg * 0.1191920 + fb * 0.9503041) / 1.08883;
    double fx = F(X), fy = F(Y), fz = F(Z);
    L = 116.0 * fy - 16.0;
    A = 500.0 * (fx - fy);
    oB = 200.0 * (fy - fz);
    static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t + 16.0 / 116.0);
}
