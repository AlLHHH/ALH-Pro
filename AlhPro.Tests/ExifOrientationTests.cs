using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>EXIF 方向 → (旋转角, 是否水平镜像) 的单测。
/// 【为什么必须有】这个映射此前在三个调用点各写一份、覆盖范围还不一样(只处理 6/8 与 6/8/3),
/// 于是方向 2/4/5/7 的手机照片在裁剪选区、涂抹标记、抠图标记上坐标全部错位 —— 而这类错位
/// 不会有任何异常,只表现为"裁出来的区域不对",极难从用户反馈里定位。
/// 收敛成一份后,用测试把 8 种方向 + 非法值全部锁住。</summary>
public class ExifOrientationTests
{
    [Theory]
    [InlineData(1, 0, false)]      // 正常
    [InlineData(2, 0, true)]       // 水平镜像
    [InlineData(3, 180, false)]    // 旋转 180
    [InlineData(4, 180, true)]     // 垂直镜像
    [InlineData(5, 90, true)]      // 转置
    [InlineData(6, 90, false)]     // 顺时针 90(手机竖拍最常见)
    [InlineData(7, 270, true)]     // 反转置
    [InlineData(8, 270, false)]    // 逆时针 90
    public void Decode_matches_exif_spec(int orientation, int expectedDegrees, bool expectedMirror)
    {
        var (deg, mirror) = ExifOrientation.Decode(orientation);
        Assert.Equal(expectedDegrees, deg);
        Assert.Equal(expectedMirror, mirror);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Right_angle_orientations_swap_dimensions(int orientation)
        => Assert.True(ExifOrientation.SwapsDimensions(orientation));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Non_right_angle_orientations_keep_dimensions(int orientation)
        => Assert.False(ExifOrientation.SwapsDimensions(orientation));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(255)]
    public void Invalid_values_are_treated_as_normal(int orientation)
    {
        Assert.False(ExifOrientation.IsDefined(orientation));
        Assert.False(ExifOrientation.NeedsTransform(orientation));
        Assert.Equal((0, false), ExifOrientation.Decode(orientation));
        Assert.False(ExifOrientation.SwapsDimensions(orientation));
    }

    [Fact]
    public void All_eight_defined_values_are_distinct_except_one_and_none()
    {
        // 1 是唯一"不需要变换"的取值;其余 7 个都必须产生非平凡变换 ——
        // 否则就是漏了一种方向(此前漏的正是 2/4/5/7)。
        Assert.False(ExifOrientation.NeedsTransform(1));
        for (int o = 2; o <= 8; o++)
        {
            Assert.True(ExifOrientation.IsDefined(o));
            Assert.True(ExifOrientation.NeedsTransform(o), $"方向 {o} 应当需要变换");
            Assert.NotEqual((0, false), ExifOrientation.Decode(o));
        }
    }
}
