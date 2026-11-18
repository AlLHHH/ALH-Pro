// 独立验证智能涂抹泛洪算法:合成纯色圆 + 白底,涂一点,看扩散是否发生。
using System;
using System.Collections.Generic;

int W = 400, H = 400;
// 合成图:白色背景,半径 120 的纯色圆(红,RGB 200,60,50),圆内带轻微渐变模拟真实图
var px = new byte[W * H * 4];
for (int y = 0; y < H; y++)
    for (int x = 0; x < W; x++)
    {
        int dx = x - 200, dy = y - 200;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        int i = (y * W + x) * 4;
        if (dist < 120)
        {
            // 圆内:轻微径向渐变(模拟明暗),但仍明显同色
            double f = 1.0 - 0.15 * (dist / 120.0);
            px[i] = (byte)(50 * f); px[i + 1] = (byte)(60 * f); px[i + 2] = (byte)(200 * f);
        }
        else
        {
            px[i] = 245; px[i + 1] = 245; px[i + 2] = 245;
        }
    }

double[] l = new double[W * H], a = new double[W * H], bb = new double[W * H];
for (int i = 0; i < W * H; i++)
    RgbToLab(px[i * 4 + 2], px[i * 4 + 1], px[i * 4], out l[i], out a[i], out bb[i]);

bool[] edge = CannyEdges(px, W, H);
Console.WriteLine($"edges={Count(edge)}");

// 模拟一笔:圆心 (200,200) 涂 3 个点(笔刷半径 6px)
var points = new List<(int, int)> { (200, 200), (205, 200), (200, 205) };
var want = new byte[W * H];
var orderArr = new int[W * H];
Array.Fill(orderArr, -1);
double brushR = 6;
double r2 = brushR * brushR;
int tolerance = 20;
double thr = 8.0 + tolerance / 100.0 * 32.0;
double thrSq = thr * thr;
int ord = 0, wv = 1;

var queue = new Queue<(int X, int Y, double rl, double ra, double rb, int want, int order)>();
foreach (var (sxp, syp) in points)
{
    for (int dy = -(int)Math.Ceiling(brushR); dy <= (int)Math.Ceiling(brushR); dy++)
    {
        int y = syp + dy;
        if (y < 0 || y >= H) continue;
        for (int dx = -(int)Math.Ceiling(brushR); dx <= (int)Math.Ceiling(brushR); dx++)
        {
            int x = sxp + dx;
            if (x < 0 || x >= W) continue;
            if (dx * dx + dy * dy > r2) continue;
            int ii = y * W + x;
            if (orderArr[ii] < ord) { want[ii] = (byte)wv; orderArr[ii] = ord; }
        }
    }
    double rl2 = 0, ra2 = 0, rb2 = 0; int refN = 0;
    for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int y = syp + dy, x = sxp + dx;
            if (y < 0 || y >= H || x < 0 || x >= W) continue;
            int ii = y * W + x;
            rl2 += l[ii]; ra2 += a[ii]; rb2 += bb[ii]; refN++;
        }
    if (refN == 0) continue;
    queue.Enqueue((sxp, syp, rl2 / refN, ra2 / refN, rb2 / refN, wv, ord));
}

while (queue.Count > 0)
{
    var (cx, cy, rl2, ra2, rb2, w2, o2) = queue.Dequeue();
    for (int d = 0; d < 4; d++)
    {
        int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
        int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        int nidx = ny * W + nx;
        if (edge[nidx]) continue;
        if (orderArr[nidx] > o2) continue;
        if (orderArr[nidx] == o2 && want[nidx] == w2) continue;
        double dl = l[nidx] - rl2, da = a[nidx] - ra2, db3 = bb[nidx] - rb2;
        double de2 = dl * dl + da * da + db3 * db3;
        if (de2 > thrSq) continue;
        want[nidx] = (byte)w2;
        orderArr[nidx] = o2;
        queue.Enqueue((nx, ny, rl2, ra2, rb2, w2, o2));
    }
}

int marked = 0, markedInCircle = 0, markedOut = 0;
for (int y = 0; y < H; y++)
    for (int x = 0; x < W; x++)
    {
        if (want[y * W + x] != 1) continue;
        marked++;
        double dist = Math.Sqrt((x - 200) * (x - 200) + (y - 200) * (y - 200));
        if (dist < 120) markedInCircle++; else markedOut++;
    }
int circleArea = (int)(Math.PI * 120 * 120);
Console.WriteLine($"marked={marked}  inCircle={markedInCircle} (circle~{circleArea})  outOfCircle={markedOut}");
Console.WriteLine(markedInCircle > circleArea * 0.7 && markedOut < circleArea * 0.05
    ? "RESULT: SPREAD OK ✅"
    : "RESULT: NO SPREAD ❌");

int Count(bool[] arr) { int c = 0; foreach (var b in arr) if (b) c++; return c; }

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

static bool[] CannyEdges(byte[] px, int w, int h)
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
    float hi = 220f, lo = 110f;
    var strong = new bool[n]; var weak = new bool[n];
    for (int i = 0; i < n; i++)
    {
        if (nms[i] >= hi) strong[i] = true;
        else if (nms[i] >= lo) weak[i] = true;
    }
    var edge = new bool[n];
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
