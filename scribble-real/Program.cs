// 真实图片验证:在 素材 (3).PNG 上模拟智能涂抹泛洪,检查扩散行为。
// 输出:标记像素数、包围盒、扩散是否越过明显边界。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
string imgPath = Path.Combine(desk, "素材 (3).PNG");
using var bmp = new Bitmap(imgPath);
int W = bmp.Width, H = bmp.Height;
Console.WriteLine($"图片: {W}x{H}");

// 读像素 BGRA
var px = new byte[W * H * 4];
var rect = new Rectangle(0, 0, W, H);
var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
unsafe
{
    var ptr = (byte*)data.Scan0.ToPointer();
    int stride = data.Stride;
    for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            int j = y * stride + x * 4;
            px[i] = ptr[j]; px[i + 1] = ptr[j + 1]; px[i + 2] = ptr[j + 2];
        }
}
bmp.UnlockBits(data);

double[] l = new double[W * H], a = new double[W * H], bb = new double[W * H];
for (int i = 0; i < W * H; i++)
    RgbToLab(px[i * 4 + 2], px[i * 4 + 1], px[i * 4], out l[i], out a[i], out bb[i]);

// 测试场景:网格 4x4 个候选点(避开边缘),每个点涂一笔,观察扩散
var seeds = new List<(int x, int y)>();
for (int gy = 0; gy < 3; gy++)
    for (int gx = 0; gx < 3; gx++)
        seeds.Add(((W * (gx + 1)) / 4, (H * (gy + 1)) / 4));

foreach (int tolerance in new[] { 10, 20, 40 })
{
    double thr = 8.0 + tolerance / 100.0 * 32.0;
    double thrSq = thr * thr;
    const double adjThrSq = 15.0 * 15.0;
    Console.WriteLine($"\n===== 容差 {tolerance} (ΔE阈={thr:F1}) =====");

    foreach (var (sx, sy) in seeds)
    {
        var want = new byte[W * H];
        var orderArr = new int[W * H];
        Array.Fill(orderArr, -1);
        double brushR = 8, r2 = brushR * brushR;
        int ord = 0, wv = 1;
        var queue = new Queue<(int X, int Y, double rl, double ra, double rb, int want, int order)>();

        // 参考色:3x3 平均
        double rl2 = 0, ra2 = 0, rb2 = 0; int refN = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int y = sy + dy, x = sx + dx;
                if (y < 0 || y >= H || x < 0 || x >= W) continue;
                int ii = y * W + x;
                rl2 += l[ii]; ra2 += a[ii]; rb2 += bb[ii]; refN++;
            }
        if (refN == 0) continue;
        rl2 /= refN; ra2 /= refN; rb2 /= refN;

        for (int dy = -(int)Math.Ceiling(brushR); dy <= (int)Math.Ceiling(brushR); dy++)
        {
            int y = sy + dy;
            if (y < 0 || y >= H) continue;
            for (int dx = -(int)Math.Ceiling(brushR); dx <= (int)Math.Ceiling(brushR); dx++)
            {
                int x = sx + dx;
                if (x < 0 || x >= W) continue;
                if (dx * dx + dy * dy > r2) continue;
                int ii = y * W + x;
                if (orderArr[ii] < ord)
                {
                    want[ii] = (byte)wv; orderArr[ii] = ord;
                    queue.Enqueue((x, y, rl2, ra2, rb2, wv, ord));
                }
            }
        }
        while (queue.Count > 0)
        {
            var (cx, cy, ql, qa, qb, w2, o2) = queue.Dequeue();
            int cidx = cy * W + cx;
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int nidx = ny * W + nx;
                if (orderArr[nidx] > o2) continue;
                if (orderArr[nidx] == o2 && want[nidx] == w2) continue;
                double adjl = l[nidx] - l[cidx], adja = a[nidx] - a[cidx], adjb = bb[nidx] - bb[cidx];
                if (adjl * adjl + adja * adja + adjb * adjb > adjThrSq) continue;
                double dl = l[nidx] - ql, da = a[nidx] - qa, db3 = bb[nidx] - qb;
                double de2 = dl * dl + da * da + db3 * db3;
                if (de2 > thrSq) continue;
                want[nidx] = (byte)w2;
                orderArr[nidx] = o2;
                queue.Enqueue((nx, ny, ql, qa, qb, w2, o2));
            }
        }

        // 统计
        int marked = 0, minX = W, minY = H, maxX = 0, maxY = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (want[y * W + x] != 1) continue;
                marked++;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        double areaPct = 100.0 * marked / (W * H);
        Console.WriteLine($"种子({sx,4},{sy,4}) Lab=({rl2,5:F1},{ra2,5:F1},{rb2,5:F1}) → 标记{marked,6}px ({areaPct,4:F1}%) 盒[{minX},{minY}]-[{maxX},{maxY}]");
    }
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
