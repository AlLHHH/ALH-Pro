namespace AlhPro.Core;

/// <summary>视频降噪滤镜链(纯逻辑,可单测)。
/// 【任务 M1 · 2026-09-13 口径变更:整体削弱,弱档大削弱】用户定案的新参数表 ——
///   弱 = nlmeans=s=3:p=3:r=3  + hqdn3d=2:1.5:3:2
///   中 = nlmeans=s=3:p=3:r=3  + hqdn3d=4:3:6:4
///   强 = 原【中】档整套      nlmeans=s=5:p=5:r=5  + hqdn3d=8:6:12:8
/// 「仅空间」(kind=1)与「仅时间」(kind=2)取同一张表里对应的那一维,**档位在三者中保持一致**
/// (老实现里「仅空间」有另一套 nlmeans 值、与组合档不同步,是这次一并修掉的非单调来源)。
/// 【对老设置的影响:不改设置、不动默认值】**老用户选「强」的处理强度会整体变轻一档**
/// (空间研究的 nlmeans 从 p7/r7 降到 p5/r5、时间维从 hqdn3d=12:10:12:8 降到 8:6:12:8),
/// 这正是本次变更的目的 —— 实测「强」档已经过降噪(见下方实测表),故不做设置迁移、只降参数。
/// 【实测依据(旧参数,2026-09,保留原记录)】960×540 压缩素材(crf30)对干净原图,PSNR / 边缘区 PSNR / 细节:
///   输入未降噪      : 36.15 / 28.83 / 72.8
///   弱 s3p3r3(旧弱): 36.38 / 28.97 / 63.1
///   中 s5p5r5(旧中): 36.42 / 28.97 / 59.5   ← 更强反而指标不再变好,细节继续掉
///   强 s7p7r7(旧强): 36.42 / 28.99 / 56.3
///   强+ s10p7r9     : 36.21 / 28.73 / 44.5   ← 更差
///   强+++ s15p9r13  : 35.53 / 28.03 / 35.3   ← 明显过降噪(细节只剩一半)
/// 时间维(真实 640×480 视频 20 帧,无参考指标 平坦区噪点 / 相邻帧抖动 / 细节):
///   不降噪 0.74 / 0.099 / 5463;hqdn3d 强 8:6:12:8 → 0.66 / 0.060 / 5507(时间维 −39%,细节几乎不损);
///   组合(空间+时间)0.62 / 0.056 / 5246 → 时间 −43%、噪点 −16%、细节仅 −4%(故采用"空间+时间"组合)。
/// 【该组合未实测,按比例外推】新表的「弱档时间维 hqdn3d=2:1.5:3:2」与「中档 hqdn3d=4:3:6:4」
/// (即旧弱档减半档/旧弱档)是**按上面这条比例关系外推**得到的,尚未真机实测;上表的
/// 8:6:12:8 与 12:10:12:8 才是实测值。待实测标定后再回填本段。</summary>
public static class VideoDenoise
{
    /// <summary>降噪方式:空间域 + 时间域联合(默认,兼容旧设置)。</summary>
    public const int KindBoth = 0;
    /// <summary>降噪方式:仅空间域(nlmeans)。</summary>
    public const int KindSpatialOnly = 1;
    /// <summary>降噪方式:仅时间域(hqdn3d)。</summary>
    public const int KindTemporalOnly = 2;

    /// <summary>档位归一:1=弱、2=中、3=强。**严格单调**是这次变更的硬要求(旧实现里"仅空间"的
    /// 三档与组合档不同步、且越界值落到"强",导致选"弱"反而不比"中"轻)。调用方只会传 1~3
    /// (videoDenoise==0 表示"关",根本不会调用滤镜链)。</summary>
    public static int StrengthOf(int strength) => strength <= 1 ? 1 : strength >= 3 ? 3 : 2;

    /// <summary>空间维 nlmeans:弱/中 = s3 p3 r3,强 = s5 p5 r5(参数越大降得越狠)。</summary>
    public static string SpatialFilter(int strength) => StrengthOf(strength) switch
    {
        3 => "nlmeans=s=5:p=5:r=5",   // 强: = 旧【中】档
        _ => "nlmeans=s=3:p=3:r=3",   // 弱、中
    };

    /// <summary>时间维 hqdn3d(亮度空间:色度空间:亮度时间:色度时间):弱 2:1.5:3:2、中 4:3:6:4、强 8:6:12:8。</summary>
    public static string TemporalFilter(int strength) => StrengthOf(strength) switch
    {
        1 => "hqdn3d=2:1.5:3:2",   // 弱:整条链最轻(旧弱档 4:3:6:4 的减半档 —— 【该组合未实测,按比例外推】)
        2 => "hqdn3d=4:3:6:4",     // 中: = 旧【弱】档
        _ => "hqdn3d=8:6:12:8",    // 强: = 旧【中】档
    };

    /// <summary>档位中文名(日志/提示用,措辞与界面下拉项一致)。</summary>
    public static string StrengthName(int strength) => strength switch
    {
        1 => "弱",
        2 => "中",
        _ => "强",
    };

    /// <summary>按降噪方式与强度拼 ffmpeg 滤镜串。
    /// <param name="strength">强度:1=弱、2=中、3=强(0 与越界值当"强"处理,与旧实现的 switch 兜底一致)。</param>
    /// <param name="kind">0=空间+时间、1=仅空间、2=仅时间。</param></summary>
    public static string Filter(int strength, int kind = KindBoth) => kind switch
    {
        KindSpatialOnly => SpatialFilter(strength),
        KindTemporalOnly => TemporalFilter(strength),
        _ => SpatialFilter(strength) + "," + TemporalFilter(strength),
    };
}
