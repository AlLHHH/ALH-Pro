// ExifFix.cs — EXIF 方向归一的【唯一实现】(图片超分 / 裁剪 / 抠图 三处共用)
using System.Drawing;
using AlhPro.Core;

namespace ALHPro;

/// <summary>把带 EXIF 方向标记的位图转正,使其与"预览显示的坐标系"一致。
/// 【为什么要独立成一个类】这段逻辑此前在三个调用点各写了一份,而且覆盖范围还不一样:
///   · ImageToolGrid.ApplyExifOrientation —— 只处理 6/8
///   · EngineService.NormalizeExif       —— 6/8/3
///   · CutoutService.ApplyExifRotation   —— 6/8/3(且原地改)
/// 于是方向 2/4/5/7(镜像 / 转置)的手机照片上:预览走 WIC(会自动应用 EXIF)而处理走
/// System.Drawing(不会应用),两套坐标系不一致 → 裁剪选区、涂抹标记、抠图标记全部错位。
/// 这类错位不抛任何异常,只表现为"裁出来的区域不对",极难从反馈里定位。
/// 现在唯一的映射来自已单测的 AlhPro.Core.ExifOrientation,三处共用同一实现。</summary>
internal static class ExifFix
{
    /// <summary>读取 EXIF 方向标签(0x0112)。无标记 / 读取失败 / 非法值一律返回 1(正常)。
    /// 【注意】请在【已经加载好的位图】上读,不要再 new Bitmap(path) 单独解码一次 ——
    /// 大图那样会白白卡住主线程(CutoutView 的注释记录过这个代价)。</summary>
    public static int ReadOrientation(System.Drawing.Image img)
    {
        try
        {
            foreach (var pi in img.PropertyItems)
                if (pi.Id == 0x0112 && pi.Value is { Length: > 0 } && AlhPro.Core.ExifOrientation.IsDefined(pi.Value[0]))
                    return pi.Value[0];
        }
        catch { /* 无 EXIF / 解码器不支持属性:按正常处理 */ }
        return 1;
    }

    /// <summary>(旋转角, 是否镜像) → System.Drawing.RotateFlipType。
    /// 这是一张 1:1 的机械映射表(与业界通用的 EXIF→RotateFlipType 对照表逐项一致),
    /// 语义与顺序的定义在 AlhPro.Core.ExifOrientation.Decode(已单测)。</summary>
    private static RotateFlipType ToRotateFlip(int orientation)
    {
        var (deg, mirror) = AlhPro.Core.ExifOrientation.Decode(orientation);
        return deg switch
        {
            90 => mirror ? RotateFlipType.Rotate90FlipX : RotateFlipType.Rotate90FlipNone,
            180 => mirror ? RotateFlipType.Rotate180FlipX : RotateFlipType.Rotate180FlipNone,
            270 => mirror ? RotateFlipType.Rotate270FlipX : RotateFlipType.Rotate270FlipNone,
            _ => mirror ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipNone,
        };
    }

    /// <summary>标记已被消费:必须清掉 0x0112,否则下游(或下一次)会按同一标记再转一次 → 双重旋转、主体偏位。</summary>
    private static void ClearMarker(Bitmap bmp)
    {
        try { bmp.RemovePropertyItem(0x0112); } catch { /* 本来就没有该标记 */ }
    }

    /// <summary>按 EXIF 方向转正并返回【新位图】;方向为 1(正常)时原样返回入参 ——
    /// 调用方若用 using 双释放,拿到同一个实例也不会重复 Dispose。</summary>
    public static Bitmap Apply(Bitmap src)
    {
        int o = ReadOrientation(src);
        if (!AlhPro.Core.ExifOrientation.NeedsTransform(o)) return src;
        var copy = new Bitmap(src);          // 不能动调用方传进来的实例(它可能还被别处引用)
        copy.RotateFlip(ToRotateFlip(o));
        ClearMarker(copy);
        return copy;
    }

    /// <summary>就地转正(不新建位图) —— 适合"这张位图本来就是我自己加载的副本"的调用点。
    /// 方向为 1 时什么都不做。</summary>
    public static void ApplyInPlace(Bitmap bmp)
    {
        int o = ReadOrientation(bmp);
        if (!AlhPro.Core.ExifOrientation.NeedsTransform(o)) return;
        bmp.RotateFlip(ToRotateFlip(o));
        ClearMarker(bmp);
    }
}
