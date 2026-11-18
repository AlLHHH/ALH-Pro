// 集成 BiRefNet-lite 的独立测试:加载 onnx、推理、后处理,验证与现有架构兼容。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

string modelPath = @"D:\deep\alh-pro\birefnet_mirror.onnx";
string testImg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "粉红豹", "素材 (2).PNG");
if (!File.Exists(modelPath)) { Console.WriteLine("模型未下载完成,等待"); return; }
if (!File.Exists(testImg)) { Console.WriteLine("测试图不存在"); return; }

Console.WriteLine("=== BiRefNet-lite ONNX 探测 ===");
using var session = new InferenceSession(modelPath);
foreach (var meta in session.InputMetadata)
    Console.WriteLine($"输入: {meta.Key} → {string.Join(",", meta.Value.Dimensions)} ({meta.Value.ElementType})");
foreach (var outMeta in session.OutputMetadata)
    Console.WriteLine($"输出: {outMeta.Key} → {string.Join(",", outMeta.Value.Dimensions)} ({outMeta.Value.ElementType})");

// 读测试图 → resize 1024x1024 → 归一化
using var src = new Bitmap(testImg);
using var resized = new Bitmap(src, 1024, 1024);
var pixels = new float[3 * 1024 * 1024];
var rect = new Rectangle(0, 0, 1024, 1024);
var data = resized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
unsafe
{
    var ptr = (byte*)data.Scan0.ToPointer();
    int stride = data.Stride;
    const float mean = 0.5f, std = 0.5f, inv = 1f / std;
    for (int y = 0; y < 1024; y++)
    {
        int row = y * 1024;
        int i = y * stride;
        for (int x = 0; x < 1024; x++)
        {
            byte* p = ptr + i + x * 4;   // BGRA
            int idx = row + x;
            pixels[idx] = (p[2] / 255f - mean) * inv;
            pixels[idx + 1024 * 1024] = (p[1] / 255f - mean) * inv;
            pixels[idx + 2 * 1024 * 1024] = (p[0] / 255f - mean) * inv;
        }
    }
}
resized.UnlockBits(data);

var inputName = session.InputMetadata.Keys.First();
var tensor = new DenseTensor<float>(pixels, new[] { 1, 3, 1024, 1024 });
var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
var sw = System.Diagnostics.Stopwatch.StartNew();
using var results = session.Run(inputs);
sw.Stop();
Console.WriteLine($"推理耗时: {sw.ElapsedMilliseconds}ms");

// 输出:取第一个 [1,1,H,W] 或 [1,H,W]
Tensor<float>? maskTensor = null;
foreach (var r in results)
{
    var t = r.AsTensor<float>();
    Console.WriteLine($"输出 {r.Name}: [{string.Join(",", t.Dimensions)}] min={t.ToArray().Min():F3} max={t.ToArray().Max():F3}");
    if (maskTensor == null && t.Dimensions.Length >= 3 && t.Dimensions[0] == 1)
        maskTensor = t;
}
if (maskTensor == null) { Console.WriteLine("无掩码输出"); return; }

var dims = maskTensor.Dimensions;
int mh = dims[^2], mw = dims[^1];
var mask = new float[mh, mw];
for (int y = 0; y < mh; y++)
    for (int x = 0; x < mw; x++)
        mask[y, x] = dims.Length == 4 ? maskTensor[0, 0, y, x] : maskTensor[0, y, x];
Console.WriteLine($"掩码 {mw}x{mh},平均={mask.Cast<float>().Average():F3}");
