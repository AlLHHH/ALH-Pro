namespace AlhPro.Core;

/// <summary>处理中的「当前帧」通道:让界面能实时看到流水线刚处理完的那一帧(逐帧预览面板用)。
/// 【为什么单独做这么一层,而不是往 VideoService 里塞回调】
///   `VideoService.ProcessAsync` 的参数表已经很长、被多处调用;为了一个"看一眼"的面板去改它的签名,
///   风险与收益不成比例。这里做成一个**静态、无锁、只写两个字符串**的窄口:
///   流水线每完成一帧写一次(成本 = 两次引用赋值),界面按自己的节奏读 ⇒ 两边完全解耦。
/// 【刻意不做线程同步】写入是"先写路径后写序号"的单向发布,读到半新半旧最多是"这一帧差一格",
///   下一帧就会纠正;加锁反而把 UI 线程拖进处理线程的节奏里。这是**显示用**的数据,不是业务数据。
/// 【代价口径】流水线侧只传**路径**(不解码、不拷贝像素);界面侧解码一张图 ≈ 5ms,且按自己的节流跑。</summary>
public static class FrameFeed
{
    /// <summary>一帧的可显示信息。<paramref name="SrcPath"/> 是**当前阶段的输入帧**(左栏显示它),
    /// <paramref name="OutPath"/> 是**当前阶段的输出帧**(右栏显示它),<paramref name="Stage"/> 是阶段名
    /// (界面据此把两侧标注成"输入(超分前)/输出(超分后)" —— 处理中**成片并不存在**,
    /// 而且超分阶段开始时拆出的源帧目录已按"边用边删"释放,所以不能假装左栏是"原片")。</summary>
    public readonly record struct Frame(string Stage, int OutIndex, int SrcIndex, string? SrcPath, string? OutPath);

    private static Frame _latest;
    private static long _seq;

    /// <summary>最新一帧(可能为空路径 = 还没有可显示的帧)。</summary>
    public static Frame Latest => _latest;

    /// <summary>序号:界面用它判断"有没有新帧",避免重复解码同一张图。</summary>
    public static long Sequence => _seq;

    /// <summary>流水线完成一帧后调用(只赋引用,不做 I/O)。</summary>
    public static void Report(string stage, int outIndex, int srcIndex, string? srcPath, string? outPath)
    {
        _latest = new Frame(stage ?? "", outIndex, srcIndex, srcPath, outPath);
        _seq++;
    }

    /// <summary>开始一批处理时清空(避免上一次任务的最后一帧残留,让用户以为这次已经出了画面)。</summary>
    public static void Reset()
    {
        _latest = default;
        _seq++;
    }
}
