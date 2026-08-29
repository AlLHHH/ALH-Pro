// ImageInfoService.cs — 图片详细信息计算(分辨率/大小/格式/色深/色域/平均RGB+亮度)
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace ALHPro;

/// <summary>一张图片的详细信息(供铅笔面板展示)。</summary>
public class ImageInfo
{
    public string Resolution { get; set; } = "—";
    public string FileSize { get; set; } = "—";
    public string Format { get; set; } = "—";
    public string BitDepth { get; set; } = "—";
    public string ColorSpace { get; set; } = "—";
    public string AvgRgb { get; set; } = "—";
    public string Luma { get; set; } = "—";
}

public static class ImageInfoService
{
    public static ImageInfo GetInfo(string path)
    {
        var info = new ImageInfo();
        try
        {
            var fi = new FileInfo(path);
            info.FileSize = FormatSize(fi.Length);
            info.Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        }
        catch { }

        try
        {
            using var img = System.Drawing.Image.FromFile(path);
            info.Resolution = $"{img.Width} × {img.Height}";
            info.BitDepth = BitDepthOf(img.PixelFormat);
        }
        catch { /* WebP 等 System.Drawing 不支持的格式 */ }

        info.ColorSpace = ReadColorSpace(path);

        try
        {
            var (r, g, b, luma) = AverageRgb(path);
            info.AvgRgb = $"R{r}  G{g}  B{b}";
            info.Luma = luma.ToString("0.0");
        }
        catch { }

        return info;
    }

    private static string BitDepthOf(PixelFormat fmt) => fmt switch
    {
        PixelFormat.Format24bppRgb => "24 位 (RGB)",
        PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb => "32 位 (RGBA)",
        PixelFormat.Format48bppRgb => "48 位 (16 位/通道)",
        PixelFormat.Format16bppArgb1555 or PixelFormat.Format16bppRgb565 => "16 位",
        PixelFormat.Format8bppIndexed => "8 位 (索引)",
        _ => fmt.ToString(),
    };

    /// <summary>解析 PNG 文件头中的 sRGB/iCCP 块;其他格式显示“未标记”。</summary>
    private static string ReadColorSpace(string path)
    {
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return "未标记 (默认 sRGB)";
        try
        {
            using var fs = File.OpenRead(path);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8 || sig[0] != 0x89 || sig[1] != 0x50)
                return "未标记";
            var lenBuf = new byte[4];
            var type = new byte[4];
            while (fs.Position + 8 <= fs.Length)
            {
                fs.Read(lenBuf, 0, 4);
                fs.Read(type, 0, 4);
                int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                var t = Encoding.ASCII.GetString(type);
                if (t == "sRGB") return "sRGB";
                if (t == "iCCP")
                {
                    var nameBytes = new byte[Math.Min(len, 200)];
                    fs.Read(nameBytes, 0, nameBytes.Length);
                    int end = Array.IndexOf(nameBytes, (byte)0);
                    var name = Encoding.ASCII.GetString(nameBytes, 0, end > 0 ? end : nameBytes.Length);
                    return "ICC: " + name;
                }
                fs.Seek(len + 4, SeekOrigin.Current); // 跳过 data + CRC
            }
        }
        catch { }
        return "未标记";
    }

    /// <summary>缩到 64x64 采样,计算平均 RGB 与亮度(Rec.601)。</summary>
    private static (byte r, byte g, byte b, double luma) AverageRgb(string path)
    {
        using var src = new System.Drawing.Bitmap(path);
        using var small = new System.Drawing.Bitmap(src, new System.Drawing.Size(64, 64));
        long rs = 0, gs = 0, bs = 0;
        for (int y = 0; y < small.Height; y++)
        {
            for (int x = 0; x < small.Width; x++)
            {
                var c = small.GetPixel(x, y);
                rs += c.R;
                gs += c.G;
                bs += c.B;
            }
        }
        int n = small.Width * small.Height;
        byte r = (byte)(rs / n), g = (byte)(gs / n), b = (byte)(bs / n);
        double luma = 0.299 * r + 0.587 * g + 0.114 * b;
        return (r, g, b, luma);
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
        : $"{bytes / 1024.0:0.0} KB";
}
