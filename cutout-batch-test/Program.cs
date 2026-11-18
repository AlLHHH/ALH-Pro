// 生产 CutoutService + BiRefNet 全 21 张验证(修复后)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ALHPro;

string modelsDir = @"D:\deep\alh-pro\发布版\engines\rembg\models";
string srcDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "粉红豹");
string outDir = @"D:\deep\alh-pro\cutout-results-biref";
Directory.CreateDirectory(outDir);
foreach (var f in Directory.GetFiles(outDir)) File.Delete(f);
EngineService.SetModelDir(modelsDir);

var files = Directory.GetFiles(srcDir)
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

var results = new List<string>();
for (int i = 0; i < files.Count; i++)
{
    string outPath = Path.Combine(outDir, $"img{i + 1:D2}.png");
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await CutoutService.CutoutAsync(files[i], outPath, "birefnet-lite",
            fgThreshold: 128, bgThreshold: 64, featherRadius: 0, edgeStrength: 0,
            gpuId: 0, scribbles: null, tolerance: 20, brushRadius: null, maxSpread: 180);
        sw.Stop();
        using var bmp = new Bitmap(outPath);
        long total = 0, opaque = 0, semi = 0;
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var d = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            var ptr = (byte*)d.Scan0.ToPointer();
            int stride = d.Stride;
            for (int y = 0; y < bmp.Height; y++)
            {
                int i2 = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte al = ptr[i2 + x * 4 + 3];
                    total++;
                    if (al >= 250) opaque++;
                    else if (al >= 10) semi++;
                }
            }
        }
        bmp.UnlockBits(d);
        results.Add($"img{i + 1:D2} 保留{100.0 * (opaque + semi) / total,5:F1}% 硬{100.0 * opaque / total,5:F1}% 耗时{sw.ElapsedMilliseconds,5}ms");
        Console.WriteLine($"OK img{i + 1:D2}: {results[^1]}");
    }
    catch (Exception ex)
    {
        results.Add($"img{i + 1:D2} 失败: {ex.Message}");
        Console.WriteLine($"FAIL img{i + 1:D2}: {ex.Message}");
    }
}
Console.WriteLine("\n===== 汇总 =====");
foreach (var r in results) Console.WriteLine(r);
