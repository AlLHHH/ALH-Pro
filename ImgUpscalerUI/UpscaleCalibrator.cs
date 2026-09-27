using System.Diagnostics;
using System.IO;
using AlhPro.Core;

namespace ALHPro;

/// <summary>**首次自动标定**:在"超分与补帧都要真跑"的那次任务里,用真引擎测出**这台机器**的超分单帧耗时,
/// 供 `AlhPro.Core.PipelineOrderPlan.Decide(...)` 判"先超分还是先补帧"(用户 2026-09-25 的硬要求:
/// 别人的机器不许用我的机器的秒/帧)。
///
/// ==== 为什么要"两点" ====(算式与出处见 `AlhPro.Core.CalibrationSample`)
/// 引擎每**进程**有一次固定地板(启动 + 模型加载 + 首次着色器/管线创建,实测约为单帧成本的 3 倍)。
/// 所以跑两次:1 帧(t1)与 N 帧(tN),`p = (tN − t1)/(N − 1)` 就是**扣掉地板的秒/帧**。
///
/// ==== 铁律 ====
/// ① 采样帧**复用已拆好的源帧**(`framesIn` 里的 JPG)⇒ **不额外调 ffmpeg**、不动用户素材;
/// ② 临时目录 `<workDir>\calib_in` / `calib_out` 在 `finally` 里删干净 —— 成功、异常、取消都删;
/// ③ **任何异常/取消都吞掉并记日志** ⇒ 返回 null,任务照跑(标定失败绝不能中断任务,更不能静默换模型);
/// ④ `runUpscale` 由调用方注入 = **本次真跑的那条引擎入口**(同一套参数:引擎/模型/倍率/降噪/GPU/分块/jpg),
///    标定与生产不各写一份;
/// ⑤ 标定耗时单独记成"准备(超分单帧耗时标定)"一个阶段,**不喂**任何逐帧进度(`progress` 只收短句),
///    免得混进"每帧速率"的记账。</summary>
internal static class UpscaleCalibrator
{
    /// <summary>上一次标定实际花掉的墙钟秒数(0 = 这次没标定/没跑起来)。
    /// 调用方用它把标定耗时写成**独立阶段**,而不是并进处理阶段的每帧速率。</summary>
    public static double LastTotalSeconds { get; private set; }

    /// <summary>上一次标定为什么没给出结果(空 = 成功或还没跑过)。用于如实告知用户。</summary>
    public static string LastRejectReason { get; private set; } = "";

    /// <summary>标定总超时(秒):两次引擎启动 + 采样帧都在这条线内。超时 ⇒ 放弃标定、保守用旧顺序。
    /// 【为什么是 180】正常(Real-CUGAN 1080p,两点法 9 帧)约 17 秒;就算落到 ONNX CPU(实测 8 秒/帧)
    /// 9 帧也只有 ~75 秒。180 秒足够,又不会让用户以为软件卡死。</summary>
    public const double TimeoutSeconds = 180.0;

    /// <summary>本次任务里标定累计花掉的墙钟秒数(可被 <see cref="ConsumeCalibratedSeconds"/> **只取一次**)。
    /// 【为什么要"取一次"】消费端 `VideoView.xaml.cs` 要把这段从 `PerfMemory` 的样本窗口里扣掉;
    /// 计数器式(取完清零)保证**只扣一次**,不重复扣、也不会把上一个任务的标定算到下一个任务头上。</summary>
    private static double _calibratedSecondsAcc;
    private static readonly object AccLock = new();

    /// <summary>取走"本次任务累计的标定耗时"并清零(**只能消费一次**)。没标定过返回 0。</summary>
    public static double ConsumeCalibratedSeconds()
    {
        lock (AccLock) { double v = _calibratedSecondsAcc; _calibratedSecondsAcc = 0; return v; }
    }

    /// <summary>仅供诊断/单测:当前累计值(不清零)。</summary>
    public static double PeekCalibratedSeconds()
    {
        lock (AccLock) return _calibratedSecondsAcc;
    }

    /// <summary>标定一次。成功返回本机实测记录(由调用方 `CalibMemory.Upsert` 落盘并复用);
    /// 失败/取消返回 null(原因写在 <see cref="LastRejectReason"/> 与日志里)。**不抛**。
    /// 【比契约 B2 多的参数】`backend`/`engine`/`model`/`engineScale`:
    ///   · `backend` = **本次定稿的超分后端**(<see cref="UpscaleBackendPlan.DescribeBackend"/> 算出来的
    ///     `ncnn-vulkan` / `onnx-dml` / `onnx-cpu`)。空白/认不出 ⇒ **直接拒收**(F1-I4 的机械保证:
    ///     后端没定稿就不许产出任何可落盘的单帧耗时);
    ///   · `engine`/`model`/`engineScale` 用于 `LocalPriceBook.TryBuild` 定位"这是哪一格单帧耗时"。</summary>
    public static async Task<LocalPrice?> MeasureAsync(
        string framesIn, int frameCount, string workDir, int srcW, int srcH,
        int sampleFrames, string machineKey, string backend,
        string engine, string model, int engineScale,
        Func<string, string, CancellationToken, Task> runUpscale,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        LastTotalSeconds = 0;
        LastRejectReason = "";

        // 【不给用户留"卡死"的印象】标定自己带一条超时线:引擎在某些驱动上会静默卡住(实测过 `-g -1` 档),
        // 而没有超时的等待会像死机。超时只取消**标定这次**(linked token),不影响整个任务。
        using var calibCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        calibCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var cct = calibCts.Token;

        string calibIn = Path.Combine(workDir, "calib_in");
        string calibOut = Path.Combine(workDir, "calib_out");
        try
        {
            // 【F1-I4】后端未定稿 ⇒ 连测都不测(测了也不能落盘,白花用户 10~20 秒)
            string bk = UpscaleBackendPlan.NormalizeBackend(backend);
            if (bk.Length == 0) return Reject($"超分后端未确认({(string.IsNullOrWhiteSpace(backend) ? "(空)" : backend)}) —— 不许在未定稿的后端上标定");
            if (string.IsNullOrWhiteSpace(machineKey)) return Reject("机器指纹为空");
            if (frameCount < CalibrationSample.MinFrames + 1) return Reject($"素材只有 {frameCount} 帧,不够两点法(需要 ≥{CalibrationSample.MinFrames + 1} 帧)");
            var frameFiles = Directory.EnumerateFiles(framesIn, "*.jpg")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            if (frameFiles.Length < CalibrationSample.MinFrames + 1) return Reject($"可用的源帧只有 {frameFiles.Length} 张,跳过标定");

            // 从【中段】等间隔取 N+1 张(避开片头黑场/片尾定格),下标由纯函数算(有单测)。
            int want = Math.Clamp(sampleFrames, CalibrationSample.MinFrames, CalibrationSample.MaxFrames);
            var idx = CalibrationSample.SampleIndices(Math.Min(frameCount, frameFiles.Length), want + 1);
            if (idx.Count < CalibrationSample.MinFrames + 1) return Reject($"素材帧数不足:只能取到 {idx.Count} 帧(需要 {CalibrationSample.MinFrames + 1} 帧)");
            int n = idx.Count - 1;                        // 1 帧那次 + n 帧那次
            long pixels = (long)Math.Max(1, srcW) * Math.Max(1, srcH);

            // 【进度】只报一句短话(界面日志区),引擎的逐帧 progress 一律不接(不然会被当成任务的帧率)。
            progress?.Report((6, "· " + LogShortText.ClampToChineseLimit(
                $"准备:正在按本机实测标定超分单帧耗时({model} {engineScale}x,{idx.Count} 帧)…")));
            AppLogger.Info($"超分单帧耗时标定(准备阶段):引擎={engine} 模型={model} 倍率={engineScale}x、后端={UpscaleBackendPlan.Label(bk)}、"
                + $"采样 {idx.Count} 帧({PixelsText(pixels)} 源)、机器指纹 {LocalPriceBook.Digest(machineKey)}"
                + $";临时目录 {calibIn} / {calibOut}");

            FreshDir(calibIn);
            FreshDir(calibOut);

            // ---- 第一次:1 帧(含每进程地板) ----
            Stage(frameFiles, calibIn, new[] { idx[0] });
            double t1 = await TimeAsync(runUpscale, calibIn, calibOut, cct).ConfigureAwait(false);
            AppLogger.Info($"超分单帧耗时标定:1 帧那次 {t1:0.###} s");

            // ---- 第二次:n 帧(地板 + n×单帧耗时) ----
            FreshDir(calibIn);
            FreshDir(calibOut);
            Stage(frameFiles, calibIn, idx.Skip(1).ToArray());
            double tN = await TimeAsync(runUpscale, calibIn, calibOut, cct).ConfigureAwait(false);
            AppLogger.Info($"超分单帧耗时标定:{n} 帧那次 {tN:0.###} s(平均 {tN / Math.Max(1, n):0.###} s/帧,含地板)");

            // 【F1-I3】样本体检:黑帧/0 字节空帧/没落地/帧数对不上 ⇒ 拒收,不落盘。
            // 判黑**复用既有口径**(EngineService.IsBlackPng → AlhPro.Core.FrameInspect.IsDefectiveFrame,
            // 与批次循环的黑帧防御同一条),这里不新造第二套判黑。
            // 【为什么必须做】ncnn 在 50 系/部分驱动上会**静默输出黑帧或 0KB 空帧且退出码 0** —— 那时
            // 两次耗时都"正常"、差值也够大,但算出来的是"引擎在空转"的偏小单帧耗时;若不拒收就会永久落盘,
            // 让判定偏向「补帧→超分」(2026-09-25 那类误判)。
            string? defect = InspectSample(calibOut, n);
            if (defect is not null) return Reject($"样本输出不合格:{defect}");

            if (!LocalPriceBook.TryBuild(model, engineScale, n, t1, tN, pixels, machineKey, bk,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), $"自动标定({engine})@{UpscaleBackendPlan.Label(bk)},{idx.Count} 帧一次性(两次进程启动,含热降频快照)",
                    out var price, out string reject))
                return Reject(reject);

            LastRejectReason = "";
            progress?.Report((6, "· " + LogShortText.ClampToChineseLimit(
                $"已按本机实测标定:{model} {engineScale}x = {price.SecondsPerFrame:0.###} 秒/帧(已扣引擎地板)")));
            return price;
        }
        catch (OperationCanceledException)
        {
            // 用户取消 vs 标定自身超时:两者对用户含义不同,必须分开写(不许含混)
            return Reject(ct.IsCancellationRequested ? "已取消" : $"标定超时(超过 {TimeoutSeconds:0} 秒,引擎可能卡住)");
        }
        catch (Exception ex)
        {
            // 引擎退出码非 0 / OOM / 未就绪 / 文件被占 … 一律吞掉:标定失败不能中断任务
            return Reject($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 【清理铁律】成功、异常、取消三条路都在这里删干净(失败也不留垃圾目录)。
            KillDir(calibIn);
            KillDir(calibOut);
            total.Stop();
            LastTotalSeconds = total.Elapsed.TotalSeconds;
            lock (AccLock) _calibratedSecondsAcc += LastTotalSeconds;   // 给 PerfMemory 扣账用(可只取一次)
            AppLogger.Info($"超分单帧耗时标定结束:耗时 {LastTotalSeconds:0.#} 秒"
                + (LastRejectReason.Length == 0 ? "(成功,结果已写入标定表)" : $"(未取得结果:{LastRejectReason});临时目录已清理")
                + " —— 这段时间单独记为「准备(超分单帧耗时标定)」,并由 VideoView 记账时从 PerfMemory 样本窗口扣除"
                + "(见 docs《本机标定-超分单帧耗时》§六.5)");
        }
    }

    /// <summary>样本输出体检:返回 null = 全好;否则中文原因。
    /// 判黑**复用既有口径** <see cref="EngineService.IsBlackPng"/> → `AlhPro.Core.FrameInspect.IsDefectiveFrame`
    /// (与批次循环的黑帧防御同一条),本方法只做"清点 + 归类",不新造像素判据。
    /// 体检失败 ⇒ 整个标定作废(不落盘、不参与判定)。</summary>
    private static string? InspectSample(string outDir, int expected)
    {
        var files = Directory.EnumerateFiles(outDir, "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var list = new System.Collections.Generic.List<CalibrationSample.SampleOutput>(files.Length);
        foreach (var f in files)
        {
            long len = 0;
            try { len = new FileInfo(f).Length; } catch { }
            bool defective;
            try { defective = EngineService.IsBlackPng(f); }   // 空/0字节/解码失败也会返回 true(缺陷)
            catch { defective = true; }
            list.Add(new CalibrationSample.SampleOutput(Present: true, Bytes: len, Defective: defective));
        }
        return CalibrationSample.OutputDefect(list, expected);
    }

    private static LocalPrice? Reject(string why)
    {
        LastRejectReason = why;
        AppLogger.Warn($"⚠ 超分单帧耗时标定未取得结果:{why} —— 本次保守用旧顺序(补帧→超分),任务继续");
        return null;
    }

    /// <summary>把指定下标(相对 frameFiles)的源帧复制进临时输入目录,命名成引擎能识别的连续帧号。
    /// 复制发生在计时之外(要测的是引擎,不是磁盘复制)。</summary>
    private static void Stage(string[] frameFiles, string inDir, int[] indices)
    {
        for (int i = 0; i < indices.Length; i++)
        {
            var src = frameFiles[Math.Clamp(indices[i], 0, frameFiles.Length - 1)];
            File.Copy(src, Path.Combine(inDir, $"frame_{i + 1:D6}.jpg"), overwrite: true);
        }
    }

    private static async Task<double> TimeAsync(Func<string, string, CancellationToken, Task> run,
        string inDir, string outDir, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await run(inDir, outDir, ct).ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed.TotalSeconds;
    }

    private static void FreshDir(string dir)
    {
        KillDir(dir);
        Directory.CreateDirectory(dir);
    }

    private static void KillDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* 清理失败只可能留下可再生临时目录 */ }
    }

    private static string PixelsText(long pixels) => LocalPrice.PixelsToText(pixels);
}
