namespace AlhPro.Core;

/// <summary>EXIF 方向标签(0x0112)的纯函数解码。
/// 【为什么要抽到这里】同一个"方向 → 变换"的映射此前在三个地方各写了一遍,覆盖范围还不一样:
/// ImageToolGrid.ApplyExifOrientation 只处理 6/8、EngineService.NormalizeExif 处理 6/8/3、
/// CutoutService.ApplyExifRotation 又是 6/8/3 —— 于是方向 2/4/5/7(镜像/转置)的手机照片,
/// 预览(WIC 会应用 EXIF)与处理(System.Drawing 不应用)坐标系不一致,裁剪选区/涂抹标记/抠图标记
/// 全部错位。同一份定义分裂成三份必然继续分叉,故收敛成一份纯函数并加单测锁住全部 8 种取值。</summary>
public static class ExifOrientation
{
    /// <summary>EXIF 2.3 规范 §4.6.4 的 8 种方向:
    /// 1=正常 2=水平镜像 3=旋转180 4=垂直镜像 5=转置(主对角镜像) 6=顺时针90 7=反转置(副对角镜像) 8=逆时针90。</summary>
    public static bool IsDefined(int orientation) => orientation is >= 1 and <= 8;

    /// <summary>是否需要做变换(1 = 正常不需要;非法值也不动)。</summary>
    public static bool NeedsTransform(int orientation) => orientation is >= 2 and <= 8;

    /// <summary>把方向归一化成"先旋转、再水平镜像"两步 —— 与 System.Drawing.RotateFlipType 的语义
    /// (先 Rotate 后 Flip)一致,调用方按此顺序实现即可覆盖全部 8 种方向。
    /// 与业界通用的 RotateFlipType 对照表逐项一致:
    /// 1=(0,false) 2=(0,true) 3=(180,false) 4=(180,true) 5=(90,true) 6=(90,false) 7=(270,true) 8=(270,false)。</summary>
    /// <returns>RotateDegrees ∈ {0,90,180,270}(顺时针);Mirror=true 表示最后再做一次水平镜像。</returns>
    public static (int RotateDegrees, bool Mirror) Decode(int orientation) => orientation switch
    {
        2 => (0, true),      // 水平镜像
        3 => (180, false),   // 旋转 180
        4 => (180, true),    // 垂直镜像(= 旋转 180 再水平镜像)
        5 => (90, true),     // 转置
        6 => (90, false),    // 顺时针 90:手机竖拍最常见
        7 => (270, true),    // 反转置
        8 => (270, false),   // 逆时针 90:手机竖拍另一种写法
        _ => (0, false),     // 1 与非法值:不改
    };

    /// <summary>变换后宽高是否互换(90/270 旋转)。调用方据此决定目标位图尺寸。</summary>
    public static bool SwapsDimensions(int orientation)
    {
        var (deg, _) = Decode(orientation);
        return deg is 90 or 270;
    }
}
