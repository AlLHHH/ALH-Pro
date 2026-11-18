// 带 alpha 但主体清晰的图测试
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ALHPro;

EngineService.SetModelDir(@"D:\deep\alh-pro\发布版\engines\rembg\models");

// 600x800 竖图:白底 + 上半部蓝色矩形(alpha=120 预存在半透明)
string pngB = @"D:\deep\alh-pro\alpha_test2.png";
using (var bmp = new Bitmap(600, 800, PixelFormat.Format32bppArgb))
{
    for (int y = 0; y < 800; y++)
        for (int x = 0; x < 600; x++)
        {
            if (x >= 150 && x < 450 && y >= 100 && y < 400)
                bmp.SetPixel(x, y, Color.FromArgb(140, 20, 80, 200));   // 蓝块,半透明
            else
                bmp.SetPixel(x, y, Color.FromArgb(255, 253, 252, 250)); // 白底
        }
    bmp.Save(pngB, ImageFormat.Png);
}

string maskOut = @"D:\deep\alh-pro\alpha2_mask.png";
string cutOut = @"D:\deep\alh-pro\alpha2_cutout.png";
await CutoutService.PreviewMaskAsync(pngB, maskOut, "isnet-general-use", 128, 64, 0);
await CutoutService.CutoutAsync(pngB, cutOut, "isnet-general-use", 128, 64, 0, 0);

using var mask = new Bitmap(maskOut);
using var cut = new Bitmap(cutOut);
// 蒙版质心(蓝块中心应≈300,250)
int mA = 0, mX = 0, mY = 0;
for (int y = 0; y < mask.Height; y += 2)
    for (int x = 0; x < mask.Width; x += 2)
    {
        int v = mask.GetPixel(x, y).R;
        if (v > 64) { mA += v; mX += x * v; mY += y * v; }
    }
Console.WriteLine($"蒙版质心: ({mX / mA:F0}, {mY / mA:F0}) (蓝块中心约 300,250)");
Console.WriteLine($"蒙版: 蓝块中心={mask.GetPixel(300,250).R} 右下={mask.GetPixel(500,600).R}");
Console.WriteLine($"输出: 蓝块中心a={cut.GetPixel(300,250).A} 右下a={cut.GetPixel(500,600).A}");
