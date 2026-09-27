using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-25 A+B】接线层(`CalibMemory` / `UpscaleCalibrator` / `VideoService` 判定点)的源码断言。
///
/// 【为什么这里只能"扫源码"】`AlhPro.Tests` 只引用 `AlhPro.Core`(纯逻辑库,可单测),**引用不到** WinUI 层;
/// 而 `CalibMemory` 要碰 `ParaPaths`/`GpuInfo`/`EngineService`(都在 UI 层)。所以:
///   · 能抽成纯逻辑的部分(单帧耗时的折算/拒收/JSON 编解码/采样下标/两点法/走哪条后端)已全部搬进 Core 并有单测;
///   · 剩下的"路径 + 文件 IO + 机器指纹 + 接线"用**源码断言**钉住关键行为(本仓库既有惯例:
///     `UpscaleOrderTests` / t21 的守卫断言都是这么做的)。
/// 断言写的是"**行为要点**"(清理由谁做、异常是否吞、判定点是否先标定再判定),不是行号 —— 免得一改版式就红。</summary>
public class LocalCalibrationWiringTests
{
    // ───────────────────────── CalibMemory:落盘 / 容错 / 去重 / 机器指纹 ─────────────────────────

    [Fact]
    public void CalibMemory_persists_to_the_settings_dir_with_a_corruption_tolerant_reader()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "CalibMemory.cs");
        Assert.Contains("ParaPaths.SettingsFile(\"engine-prices.json\")", src);   // 与 PerfMemory 同目录
        Assert.Contains("public static IReadOnlyList<LocalPrice> All()", src);
        Assert.Contains("public static void Upsert(LocalPrice price)", src);
        Assert.Contains("public static string MachineKeyOf()", src);
        // 解析/序列化复用 Core 的纯逻辑(容错规则只写一份,有单测)
        Assert.Contains("LocalPriceBook.ParseJson(", src);
        Assert.Contains("LocalPriceBook.ToJson(", src);
        Assert.Contains("LocalPriceBook.Upsert(", src);
        // 先写临时文件再原子替换(写一半被杀不会把整张表废掉)
        Assert.Contains(".tmp", src);
        Assert.Contains("File.Move(tmp, FilePath, overwrite: true)", src);
        // 读盘失败 → 空表 + 不抛(异常路径必须自己兜住,不许漏出去中断任务)
        Assert.Contains("catch (Exception ex)", src);
        Assert.Contains("Array.Empty<LocalPrice>()", src);
        // 机器指纹 = GPU 名称 + 引擎可执行文件标识(名字 + 长度 + 最后写入时间)
        Assert.Contains("GpuInfo.GetAdapterNames()", src);
        Assert.Contains("FindRealESRGAN", src);
        Assert.Contains("FindWaifu2x", src);
        Assert.Contains("FindRealCugan", src);
        Assert.Contains("fi.Length", src);
        Assert.Contains("fi.LastWriteTimeUtc.Ticks", src);
    }

    // ───────────────────────── UpscaleCalibrator:采样 / 计时 / 清理 / 吞异常 ─────────────────────────

    [Fact]
    public void Calibrator_samples_existing_frames_and_always_cleans_up()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "UpscaleCalibrator.cs");
        // ① 复用已拆好的源帧:采样只读 framesIn 里的 jpg,**不额外起任何进程**(更不会去调 ffmpeg 重新拆帧)
        Assert.Contains("Directory.EnumerateFiles(framesIn, \"*.jpg\")", src);
        Assert.DoesNotContain("Process.Start", src);          // 采样阶段不自己起进程
        Assert.DoesNotContain("ExtractFramesAsync", src);     // 不复用拆帧入口 ⇒ 不会二次拆帧/覆盖源帧
        Assert.DoesNotContain("File.Delete(frameFiles", src); // 只读源帧,绝不改动用户的中间帧
        // ② 采样点由 Core 的纯函数决定(有单测),并优先取中段
        Assert.Contains("CalibrationSample.SampleIndices(", src);
        // ③ 临时目录名与位置
        Assert.Contains("Path.Combine(workDir, \"calib_in\")", src);
        Assert.Contains("Path.Combine(workDir, \"calib_out\")", src);
        // ④ 两次进程各测一次(1 帧 / N 帧),用 Stopwatch
        Assert.Contains("Stopwatch", src);
        Assert.Contains("LocalPriceBook.TryBuild(", src);
        // ⑤ 清理写在 finally 里(成功/异常/取消三条路都删)
        int finallyAt = src.LastIndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyAt > 0, "必须有 finally 块");
        string finallyBlock = src[finallyAt..];
        Assert.Contains("KillDir(calibIn)", finallyBlock);
        Assert.Contains("KillDir(calibOut)", finallyBlock);
        Assert.Contains("LastTotalSeconds", finallyBlock);
        // ⑥ 任何异常/取消都吞掉:只记日志 + 返回 null(绝不把标定失败变成任务失败)
        Assert.Contains("catch (OperationCanceledException)", src);
        Assert.Contains("catch (Exception ex)", src);
        Assert.Contains("LastRejectReason", src);
        // ⑦ 超时保护:引擎卡住时不让用户白等(只取消标定这次,不影响任务)
        Assert.Contains("CreateLinkedTokenSource(ct)", src);
        // ⑧ UI 只收一句短话(逐帧 progress 不接进来,免得混进"每帧速率")
        Assert.Contains("LogShortText.ClampToChineseLimit(", src);
    }

    // ───────────────────────── VideoService 判定点:先标定、再判定、失败不许静默 ─────────────────────────

    [Fact]
    public void Decision_point_calibrates_first_then_decides_with_the_local_book()
    {
        string svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        int calibAt = svc.IndexOf("UpscaleCalibrator.MeasureAsync(", StringComparison.Ordinal);
        int decideAt = svc.IndexOf("localPrices: sameBackendBook", StringComparison.Ordinal);
        Assert.True(calibAt > 0, "判定点必须先调用标定器");
        Assert.True(decideAt > 0, "判定必须带上本机标定表");
        Assert.True(calibAt < decideAt, "标定必须发生在判定之前(先标定、再判定)");
        // 【F1-I1 · 结构断言】"探测 + 设备定稿"整段(`if (upscaleRuns) { … }`)必须在**标定之前** ——
        // 这就是 F1 的修法本身:否则标定可能测到生产根本不会走的后端(ncnn 黑帧 ⇒ 偏小错价永久落盘)。
        int probeAt = svc.IndexOf("if (upscaleRuns)", StringComparison.Ordinal);
        Assert.True(probeAt > 0, "必须先有'探测 + 设备定稿'整段(if (upscaleRuns))");
        Assert.True(probeAt < calibAt, "探测/设备定稿必须在标定之前(否则标定可能测到生产不走的后端)");

        // 判定入口:本机标定表 + 本机机器指纹(内置表不再作为判据来源)
        Assert.Contains("CalibMemory.All()", svc);
        Assert.Contains("CalibMemory.MachineKeyOf()", svc);
        Assert.Contains("machineKey: calibMachineKey", svc);
        // 【I2 · 两层】① 判定喂的表已按后端筛过;② 后端本身也传进 Core,由 Resolve 机械保证"不串后端"。
        Assert.Contains("var sameBackendBook = calibBook", svc);
        Assert.Contains("backend: calibBackend", svc);
        Assert.Contains("AlhPro.Core.LocalPriceBook.Resolve(calibBook, model, calibEngineScale, calibMachineKey, calibBackend).Usable", svc);
        Assert.DoesNotContain("LookupUpscaleSecondsPerFrame", svc);   // 生产路径不许再读他机表

        // 标定只做一次,且结果落盘复用
        Assert.Contains("CalibMemory.Upsert(cp)", svc);
        Assert.Contains("calibBook = CalibMemory.All();", svc);

        // 标定失败 ⇒ 用户可见短话(不许静默)+ 说明这次按旧顺序
        Assert.Contains("UpscaleCalibrator.LastRejectReason", svc);
        Assert.Contains("保守按「补帧→超分」", svc);

        // 【F2 · 两半必须成对】标定耗时单独记一个阶段,**并且**消费端(记账处)真的把它从
        // PerfMemory 的每帧成本样本窗口里扣掉。只写日志不扣账 = 那句"不参与每帧成本"就是错话
        // (t28/t29 判 medium 的正是这一条)。
        Assert.Contains("阶段耗时(准备·超分单帧耗时标定)", svc);
        Assert.DoesNotContain("PerfMemory.Record", svc);              // 判定点/标定路径不许自己记账
        string view = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("taskSpan.TotalSeconds - encSeconds - calibSeconds", view);   // 真的从样本窗口扣掉

        // 【R1 · 2026-09-25 修订】归零点必须**每任务一次**,不能在 per-video 的 ProcessVideoAsync 里 ——
        // 否则多视频任务里:视频 1 标定累计 → 视频 2 的入口把值消费掉丢弃 → 记账时 calibSeconds=0
        // (标定墙钟照样进样本,与文档/日志的"已扣除"相反)。
        // ① 防回退:VideoService(每个视频都跑)里**不许**再出现消费点;
        Assert.DoesNotContain("ConsumeCalibratedSeconds", svc);
        // ② 归零点在 VideoView 的任务入口:清零 → 逐 item 循环 → 记账处扣减(三个位置严格有序)
        int clearAt = view.IndexOf("UpscaleCalibrator.ConsumeCalibratedSeconds();", StringComparison.Ordinal);
        int perItemAt = view.IndexOf("for (int i = 0; i < items.Length; i++)", StringComparison.Ordinal);
        int accountAt = view.IndexOf("double calibSeconds = UpscaleCalibrator.ConsumeCalibratedSeconds();", StringComparison.Ordinal);
        int recordAt = view.IndexOf("PerfMemory.Record(", StringComparison.Ordinal);
        Assert.True(clearAt > 0, "任务入口必须有归零点");
        Assert.True(perItemAt > clearAt, $"归零点必须在逐 item 循环之前(clear={clearAt}, loop={perItemAt})");
        Assert.True(accountAt > perItemAt, $"扣减点必须在逐 item 循环之后(记账是全任务一次)(account={accountAt})");
        Assert.True(recordAt > accountAt, $"扣减必须在 PerfMemory.Record 之前(record={recordAt})");
        // ③ 任务起点与归零点相邻(相对顺序证据:taskStart 先出现)
        Assert.True(view.IndexOf("var taskStart = DateTime.Now;", StringComparison.Ordinal) < clearAt);

        // 【不许假精度】未标定时 u=0 只是"没采信单帧耗时",界面**不许**报基于它算出的"先超分反而慢 X%"
        Assert.Contains("本机还没标定超分单帧耗时,保守不切换", svc);
        Assert.Contains("orderPlan.Measured || interpScale < 2", svc);

        // 【R2 · 2026-09-25 修订】后端不匹配要像"另一台机器"那样明写出来:判定理由(Core)+ 界面短话
        string core = ReadRepoFile("AlhPro.Core", "PipelineOrderPlan.cs");
        Assert.Contains("另一后端", core);
        Assert.Contains("本机这一格是在另一后端", svc);
    }

    /// <summary>验收 §4.9:文档必须真的写明**公式 / 口径 / 失败路径 / §5 的四条已知限制**(逐条在场,不是提一句)。
    /// 这条测试把"文档要写全"变成机器可查的,免得以后有人把限制删掉。</summary>
    [Fact]
    public void The_doc_states_the_formula_the_scope_the_failure_paths_and_all_four_known_limits()
    {
        string doc = ReadRepoFile("docs", "本机标定-超分单帧耗时.md");

        // 公式(两点法,含两次耗时与 N)
        Assert.Contains("t1 = F + 1·p", doc);
        Assert.Contains("tN = F + N·p", doc);
        Assert.Contains("(tN − t1) / (N − 1)", doc);
        // 口径:内置表只作资料、判定只吃本机标定、按面积折算到 1080p
        Assert.Contains("只作资料与回归基线", doc);
        Assert.Contains("2073600", doc);
        Assert.Contains("机器指纹", doc);
        // 失败路径
        Assert.Contains("失败路径", doc);
        Assert.Contains("标定失败", doc);
        Assert.Contains("超时", doc);
        Assert.Contains("素材帧数不足", doc);
        // §5 四条已知限制(逐条)
        Assert.Contains("第 1 批", doc);                     // §5.1 黑帧中途切 ONNX 的漂移
        Assert.Contains("VideoPipeline.cs", doc);            // §5.2 ETA 常数仍是开发机数字
        Assert.Contains("采样帧来自本次素材", doc);            // §5.3 换素材/面积折算误差
        Assert.Contains("热降频", doc);                      // §5.4 两次启动的地板不同 ⇒ 快照
        Assert.Contains("快照", doc);
        // 本轮额外如实标注的那条(PerfMemory 样本仍含标定时间 + 未验证清单)
        Assert.Contains("LastTotalSeconds", doc);
        Assert.Contains("未验证", doc);
    }

    /// <summary>内置表确实退出了判据:生产入口不再取它的值(源码断言),而单测从行为上证明
    /// "同一组入参、只改 `localPrices` 就能翻转结论"。</summary>
    [Fact]
    public void The_builtin_table_no_longer_decides_anything()
    {
        string core = ReadRepoFile("AlhPro.Core", "PipelineOrderPlan.cs");
        Assert.DoesNotContain("double? up = LookupUpscaleSecondsPerFrame(model, engineScale, out string prov);", core);
        Assert.Contains("var look = LocalPriceBook.Resolve(localPrices, model, engineScale, machineKey, backend);", core);
        Assert.Contains("IEnumerable<LocalPrice>? localPrices = null", core);
        // 类注释必须写明"他机实测,只作资料与回归基线"
        Assert.Contains("只作资料与回归基线", core);
        Assert.Contains("他机实测", core);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
