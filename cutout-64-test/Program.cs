// 查看增强后的掩码 9 宫格(确认锐化后主体/背景是否拉开)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

string modelPath = @"D:\deep\alh-pro\发布版\engines\rembg\models\isnet-general-use.onnx";
string src = @"C:\Users\AlL\Desktop\f3fe01f17d593b419134c20a7443a8f8.png";
using var session = new InferenceSession(modelPath);
using var bmp = new Bitmap(src);
using var resized = new Bitmap(bmp, 1024, 1024);
var pixels = new float[3 * 1024 * 1024];
var rect = new Rectangle(0, 0, 1024, 1024);
var data = resized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
unsafe
{
    var ptr = (byte*)data.Scan0.ToPointer();
    int stride = data.Stride;
    for (int y = 0; y < 1024; y++)
    {
        int row = y * 1024;
        int i = y * stride;
        for (int x = 0; x < 1024; x++)
        {
            byte* p = ptr + i + x * 4;
            int idx = row + x;
            pixels[idx] = (p[2] / 255f - 0.5f) * 2f;
            pixels[idx + 1024 * 1024] = (p[1] / 255f - 0.5f) * 2f;
            pixels[idx + 2 * 1024 * 1024] = (p[0] / 255f - 0.5f) * 2f;
        }
    }
}
resized.UnlockBits(data);
var inputName = session.InputMetadata.Keys.First();
var tensor = new DenseTensor<float>(pixels, new int[] { 1, 3, 1024, 1024 });
using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
var outT = results.First().AsTensor<float>();

// 原始掩码 + 增强(γ=1)对比
Console.WriteLine("原始掩码 9 宫格(0.5 基准):");
for (int gy = 1; gy <= 3; gy++)
{
    var row = "";
    for (int gx = 1; gx <= 3; gx++)
    {
        int x = 1024 * gx / 4, y = 1024 * gy / 4;
        float v = 1f / (1f + (float)Math.Exp(-outT[0, 0, y, x]));
        row += $"{v:F3} ";
    }
    Console.WriteLine($"  {row}");
}
Console.WriteLine("增强后(γ=1, k=6) 9 宫格:");
for (int gy = 1; gy <= 3; gy++)
{
    var row = "";
    for (int gx = 1; gx <= 3; gx++)
    {
        int x = 1024 * gx / 4, y = 1024 * gy / 4;
        float v = 1f / (1f + (float)Math.Exp(-outT[0, 0, y, x]));
        double e = 1.0 / (1.0 + Math.Exp(-6 * (v - 0.5)));
        row += $"{e:F3} ";
    }
    Console.WriteLine($"  {row}");
}
