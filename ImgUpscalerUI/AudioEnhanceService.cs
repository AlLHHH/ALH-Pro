// AudioEnhanceService.cs — HT-Demucs 音乐分离/降噪(纯 C#,ONNX Runtime,MIT 可发布)
// 功能:音乐源分离(人声/伴奏/鼓/贝斯/其他)、卡拉OK(去人声)、仅人声/仅伴奏输出。
// 模型:engines/demucs/htdemucs.onnx(158MB fp16,MIT,StemSplitio/htdemucs-onnx)
// 流程:输入任意音频 → ffmpeg 转 44.1kHz 立体声 WAV → 分块(7.8s+重叠窗)推理 → 输出所选轨 WAV。
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class AudioEnhanceService
{
    const int SAMPLE_RATE = 44100;
    const double SEGMENT_S = 7.8;
    const int N_SAMPLES = (int)(SEGMENT_S * SAMPLE_RATE);   // 343,980
    const int N_CHANNELS = 2;

    /// <summary>分离目标:0=人声,1=伴奏(去人声),2=鼓,3=贝斯,4=其他,5=仅人声+伴奏重混增强(保原声场)。</summary>
    public static string[] TargetLabels = { "人声", "伴奏(去人声)", "鼓", "贝斯", "其他", "人声+伴奏" };

    public static string? FindModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "demucs");
        // 优先"人声微调版"(htdemucs_ft_vocals):专门优化人声提取更干净 → 伴奏=原曲−人声 残差更小
        // 实测:标准版伴奏残留人声(分离误差),ft_vocals 版明显改善。缺失时回退标准 htdemucs。
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, "htdemucs_ft_vocals*.onnx", SearchOption.AllDirectories))
                return f;
            foreach (var f in Directory.EnumerateFiles(root, "htdemucs*.onnx", SearchOption.AllDirectories))
                return f;
        }
        var direct = Path.Combine(EngineService.EnginesDir, "htdemucs.onnx");
        return File.Exists(direct) ? direct : null;
    }

    // 【修复】会话/设备缓存(替换原来无用的 _sessions/_locks):
    // 设备可用性判定不再自带一张表 —— 原先这里有个 _dmlBad 永久闩锁(一次瞬时失败就把该设备
    // 在【整个进程】内判死,只有重启软件能恢复)。现改为共用 EsrganOnnxService 的连击计数:
    // 同设备【连续】3 次瞬时失败才算不可用,GPU 成功一次即清零(与超分/补帧同一口径)。
    // _cpuSession: 复用的 CPU 会话(DML 失败/纯 CPU 时用),避免每个分块都新建 158MB 模型 → 同样灾难级慢;
    // _gpuSession: 按 (引擎 -g 号, DML 设备号) 复用的 GPU 会话(避免每批新建;音频单次任务并发低,仍加锁保护)。
    // 【B4】两个号都要记:同名卡的映射理论上会变(设备插拔/驱动重排),只用 gpuId 做键会把"上一次用 A 卡建的会话"
    // 拿去给 B 卡用 —— 所以 dmDevice 变了一样重建。
    private static InferenceSession? _cpuSession;
    private static string? _cpuSessionPath;
    private static readonly object _sessionLock = new();
    private static InferenceSession? _gpuSession;
    private static int _gpuSessionId = int.MinValue;
    private static int _gpuSessionDm = int.MinValue;
    private static bool _gpuSessionOnDml;   // 【B4】那个缓存槽里的会话到底挂上 DML 没有(见 RunCore)

    /// <summary>分离音频。input=任意音频(程序内先用 ffmpeg 转成 44.1k stereo wav);输出所选轨 wav。
    /// target:0人声 1伴奏 2鼓 3贝斯 4其他 5重混 6分离(输出 人声+伴奏 两文件)。
    /// vocalStrength=0~1:人声轨混合比例(0=原曲,0.5=一半,1=纯人声)——用于"人声强度"滑条手动调整。</summary>
    public static async Task SeparateAsync(string inputWav, string outputWav, int target,
        int gpuId = -1, float vocalStrength = 1f,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var modelPath = FindModel()
            ?? throw new FileNotFoundException("缺少人声分离模型:HT-Demucs。");
        await Task.Run(() => RunCore(inputWav, outputWav, target, modelPath, gpuId, vocalStrength, progress, ct), ct);
        progress?.Report((100, "完成"));
    }

    private static void RunCore(string inputWav, string outputWav, int target, string modelPath, int gpuId,
        float vocalStrength, IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        // 【B4 · 2026-09-23 键空间修复】本方法收到的是【引擎 -g 编号】,而 DirectML 连击表/建会话用的是
        // 【DML 设备号】—— 两者在双卡机/带核显的机器上**不是同一个数**(EsrganOnnxService 的连击表注释
        // 明确写着"全表只认 DML 号,绝不混入引擎 -g 编号")。原先这里把 gpuId 直接当 DML 号用:
        //   · 熔断查询查的是别人的键 ⇒ 熔断永远查不出来(每个任务都重新白试一遍,实测每次白等 2~11 秒
        //     —— 这正是本轮 B4 那条);
        //   · 连击的记/清也落在别人的键上 ⇒ 计数永远攒不到上限。
        // 现在解析一次(顺带省掉一次无缓存的 DXGI 枚举),后面只认 dmDevice;-1 = 本机匹配不到该卡 ⇒ 不走 GPU。
        int dmDevice = gpuId >= 0 ? EngineService.ToDmlDevice(gpuId) : -1;
        // 【修复】复用会话(原先每次/每批都新建 158MB 模型,慢且 DML 失败时每分块重建):
        // 优先复用 GPU 会话;该设备连续失败达上限时才复用 CPU 会话。音频单次任务并发低,用锁串行化创建。
        InferenceSession session;
        // 【本轮 · 结论落盘】问一次"本机有没有生效的'这个模型 + 这张卡跑不了 DML'结论"。
        // 【为什么还要问落盘】上面那张连击表是**进程内**的:连吃 3 次才熔断、重启即忘 ⇒ 每个应用会话都要
        // 重新白试一遍(实测日志 2026-09-23 的 21:41/21:42/23:07/23:28 四次 DmlFusedNode_0_0 失败,每次白等 5~9 秒)。
        // 【契约】没结论 / 结论过期 / 结论文件损坏 / 读取出错 ⇒ 这里返回 false ⇒ **照现状试一次**;
        // 绝不会把没测过的机器一刀切成 CPU(那样会把能跑的机器也拖成慢速 CPU)。
        // 命中时的日志由 EsrganOnnxService 记(每个"模型+设备"只报一次,避免批量处理时刷屏)。
        bool verdictDenied = EsrganOnnxService.AudioDmlVerdictDenied(dmDevice, modelPath);
        bool onCpu = dmDevice < 0
            || EsrganOnnxService.DmlDeviceUnusable(dmDevice, EsrganOnnxService.DmlDomain.Audio)
            || verdictDenied;
        // 【B4】本次实际用的是不是 DirectML 会话:建会话失败时那个缓存槽里躺的其实是**CPU 后端**的会话,
        // 不能再按"GPU 会话"对待(否则 CPU 推理失败会被记成 GPU 失败、还会被拉进重试路径)。
        bool sessionOnDml = false;
        lock (_sessionLock)
        {
            if (!onCpu)
            {
                if (_gpuSession == null || _gpuSessionId != gpuId || _gpuSessionDm != dmDevice)
                {
                    _gpuSession?.Dispose();
                    _gpuSession = null;
                    var opts = new SessionOptions();
                    bool onDml = false;
                    try
                    {
                        opts.AppendExecutionProvider_DML(dmDevice);
                        onDml = true;
                    }
                    catch (Exception dmlEx)
                    {
                        // 【第 1 项】原先是 `catch { }` —— DirectML 建会话失败在音频路径上一点痕迹都没有,
                        // 诊断包里只能看到后面的 8007000E 推理失败,分不清是"provider 没起来"还是"起来了但显存不足"。
                        // 现在完整记录:设备号/HRESULT(十六进制)/异常类型/Message 首行/InnerException 链 + 定性。
                        // 不重抛(保持既有"音频 DML 不可用回退 CPU"的行为不变),但必须留痕。
                        EsrganOnnxService.LogDmlFailure("AudioEnhanceService.AppendExecutionProvider_DML", dmDevice, dmlEx);
                        // 【B4】建会话失败是结构性的(provider 挂不上):直接判该设备在音频域不可用(进程内闩锁),
                        // 否则下一个任务还会照样白试一次(实测白等 2~11 秒/任务)。
                        EsrganOnnxService.NoteDmlSessionCreationFailure(dmDevice, EsrganOnnxService.DmlDomain.Audio);
                        // 【本轮 · 结论落盘】建会话失败 = 结构性失败(provider 都挂不上),进程内闩锁只保到本次运行;
                        // 落盘一份"这台卡在这个模型上跑不了 DML"的否定结论,下次启动才不会再白等一遍。
                        EsrganOnnxService.NoteAudioDmlDenial(dmDevice, modelPath,
                            "建会话失败(AppendExecutionProvider_DML): " + dmlEx.GetType().Name);
                        AppLogger.Warn($"⚠ 音频分离的 DirectML 会话建不起来(DML 设备 {dmDevice}):本次及本机后续任务直接走 CPU 会话"
                            + "(结论已落盘,重启软件也不会再白试;若想用上显卡加速,先更新显卡驱动/关闭占用显存的程序,"
                            + "或删掉 settings\\dml-verdicts.txt 强制重试)");
                    }
                    // 【第 4 项①】DML 没起来 → 这是 CPU 会话,显式限制 ONNX 线程数(见 ApplyConservativeCpuThreads)
                    if (!onDml) EsrganOnnxService.ApplyConservativeCpuThreads(opts);
                    _gpuSession = new InferenceSession(modelPath, opts);
                    _gpuSessionId = gpuId;
                    _gpuSessionDm = dmDevice;
                    _gpuSessionOnDml = onDml;
                }
                session = _gpuSession;
                sessionOnDml = _gpuSessionOnDml;
            }
            else
            {
                if (_cpuSession == null || _cpuSessionPath != modelPath)
                {
                    _cpuSession?.Dispose();
                    _cpuSession = new InferenceSession(modelPath, EsrganOnnxService.NewCpuSessionOptions());
                    _cpuSessionPath = modelPath;
                }
                session = _cpuSession;
            }
        }

            // 读取 WAV(44.1k stereo float32)
            var (mix, samples) = ReadWav(inputWav);
            int total = samples;
            // 内存预估(4 轨输出缓冲/选择/拷贝等 ≈ 80B/采样):超阈值提前明确报错,避免整进程 OOM
            double estMB = total * 80.0 / (1024.0 * 1024.0);
            if (estMB > 2500)
                throw new InvalidOperationException(
                    $"音频过长({(double)total / SAMPLE_RATE / 60.0:0.##} 分钟),AI 分离内存需求约 {estMB / 1024.0:0.#}GB,可能超出本机内存 — 请用波形两端裁剪缩短后再试,或分段处理。");
            int overlap = N_SAMPLES / 4;
            int stride = N_SAMPLES - overlap;
            int nChunks = Math.Max(1, (total + stride - 1) / stride);
            var window = MakeWindow(N_SAMPLES, overlap);

            // 输出累积(4 轨 × 2 声道 × total)
            var outBuf = new float[4, N_CHANNELS, total];
            var weight = new float[total];

            for (int i = 0; i < nChunks; i++)
            {
                ct.ThrowIfCancellationRequested();
                int start = i * stride;
                int end = Math.Min(start + N_SAMPLES, total);
                var chunk = new float[1 * N_CHANNELS * N_SAMPLES];
                for (int c = 0; c < N_CHANNELS; c++)
                    for (int s = 0; s < end - start; s++)
                        chunk[0 * N_CHANNELS * N_SAMPLES + c * N_SAMPLES + s] = mix[c, start + s];
                // 尾部补零(不足 N_SAMPLES)
                var tensor = new DenseTensor<float>(chunk, new[] { 1, N_CHANNELS, N_SAMPLES });
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
                try
                {
                    // 【第 4 项②】CPU 会话的推理进"本进程内 CPU 计算"作用域(Job 上限压到 CpuComputeCapPct);
                    // GPU 会话不进作用域,上限维持原值(不拖慢正常情况)。
                    // ★ B4:判据用 sessionOnDml(真挂上 DML 了才不进作用域)—— 建会话失败时那个会话是 CPU 后端。
                    using (sessionOnDml ? null : SafeRender.EnterOwnCpuCompute())
                        results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("mix", tensor) });
                    // GPU 真跑成功 → 清零该设备连击(偶发抖动不该累积成"设备不可用")。★ 用 DML 号(B4)
                    if (sessionOnDml) EsrganOnnxService.ClearDmlStrikes(dmDevice, EsrganOnnxService.DmlDomain.Audio);
                    // 【本轮 · 结论落盘】成功也要落一句结论:否则"上一次失败的落盘结论"会一直留到 TTL 过期,
                    // 让一台本来能跑的机器在重启后被判成跑不了。账本里已是成功结论时这里不会重复写文件
                    // (推理是按分块回调的,一首歌四十来个分块)。★ 用 DML 号
                    if (sessionOnDml) EsrganOnnxService.NoteAudioDmlSuccess(dmDevice, modelPath);
                }
                catch (Exception ex) when (sessionOnDml)
                {
                    // 记一次瞬时失败(连续 3 次才判该设备不可用,与超分/补帧同口径),本次任务改走【复用的 CPU 会话】。
                    // 关键:必须把 session 换掉。原先只写了一张闩锁表、session 从没重新赋值,
                    // 于是"后续分块直接走 CPU"这句日志是假的 —— 剩下每个分块都照样白试一次注定失败的
                    // GPU 再转 CPU(闩锁要到【下一个任务】才生效)。
                    // ★ 连击表只认 DML 设备号,原先传的是引擎 -g 号(B4):记的是别人的键 ⇒ 永远攒不到上限。
                    bool unusable = EsrganOnnxService.NoteDmlTransientFailure(dmDevice, EsrganOnnxService.DmlDomain.Audio);
                    // 【本轮 · 结论落盘】把这次失败记进**跨进程**账本:否则连击表重启就忘,
                    // 每个应用会话都要重新白试一遍(真机日志:同一天四次 DmlFusedNode_0_0,每次白等 5~9 秒)。
                    // 只有进程内已判死(unusable)时才连带把落盘结论一起判死 —— 一次瞬时失败不该跨进程重罚
                    // (判错代价不对称:多试两次最多白花几秒,一次误判会让能跑的机器整天跑 CPU)。
                    bool verdictLatched = EsrganOnnxService.NoteAudioDmlFailure(dmDevice, modelPath,
                        "推理失败(session.Run): " + ex.GetType().Name, unusable);
                    // 【第 1 项】把 HRESULT 十六进制/异常类型/Message 首行/InnerException 链一并记下:
                    // 原先只有 Message 首行,诊断包里分不清显存不足 0x8007000E 还是设备摘除 0x887A0005/6。
                    AppLogger.Warn($"⚠ 音频分离 GPU 推理失败 — {EsrganOnnxService.DescribeDmlFailure("AudioEnhanceService session.Run(GPU)", dmDevice, ex)}"
                        + $"(引擎 -g {gpuId} → DML 设备 {dmDevice});本次任务改用 CPU 会话"
                        + (verdictLatched
                            ? ";该 GPU 在本机已被判定跑不了这个模型,结论已落盘(重启软件也不再白试;删掉 settings\\dml-verdicts.txt 可强制重试)"
                            : (unusable ? ";该 GPU 连续失败已达上限,本进程内不再尝试" : "(GPU 成功一次即复位计数)")));
                    InferenceSession cpuS;
                    lock (_sessionLock)
                    {
                        if (_cpuSession == null || _cpuSessionPath != modelPath)
                        {
                            _cpuSession?.Dispose();
                            _cpuSession = new InferenceSession(modelPath, EsrganOnnxService.NewCpuSessionOptions());
                            _cpuSessionPath = modelPath;
                        }
                        cpuS = _cpuSession;
                    }
                    session = cpuS;
                    // ★ B4:判据只留 sessionOnDml —— session 已经换成 CPU 会话,它才决定"还要不要再试 GPU"。
                    // (原先是 onCpu=true 兼作判据,而 onCpu 只在建会话那一段被读 ⇒ 那个赋值其实是空转。)
                    sessionOnDml = false;
                    // 【第 4 项②】转 CPU 后的重算同样进 CPU 计算作用域(上限压到 CpuComputeCapPct)
                    using (SafeRender.EnterOwnCpuCompute())
                        results = cpuS.Run(new[] { NamedOnnxValue.CreateFromTensor("mix", tensor) });
                }
                using (results)
                {
                    var stems = results!.First().AsTensor<float>();   // (1,4,2,N)
                    int clen = end - start;
                    for (int st = 0; st < 4; st++)
                        for (int c = 0; c < N_CHANNELS; c++)
                            for (int s = 0; s < clen; s++)
                                outBuf[st, c, start + s] += stems[0, st, c, s] * window[s];
                    for (int s = 0; s < clen; s++) weight[start + s] += window[s];
                }
                progress?.Report(((int)((double)(i + 1) / nChunks * 90), $"分离 {i + 1}/{nChunks} 段..."));
            }

            // 归一化权重 + 选轨输出
            int outCount = target == 6 ? 2 : target == 7 ? 4 : 1;   // 6=人声+伴奏;7=四轨全部;其余 1 个
            var sel = new float[outCount, N_CHANNELS, total];
            for (int c = 0; c < N_CHANNELS; c++)
                for (int s = 0; s < total; s++)
                {
                    float w = Math.Max(weight[s], 1e-8f);
                    // 实测(用户试听 10s《GIRL LIKE ME》):单体输出轨序 = 轨0:伴奏 · 轨1/2:其他 · 轨3:人声!
                    // (官方"drums/bass/other/vocals"标注与引擎实际输出不符——以听感为准)
                    float acc1 = outBuf[0, c, s] / w;   // 伴奏(轨0)
                    float o1 = outBuf[1, c, s] / w;     // 其他1
                    float o2 = outBuf[2, c, s] / w;     // 其他2
                    float v = outBuf[3, c, s] / w;      // 人声(轨3)
                    float org = mix[c, s];              // 原曲
                    float acc = acc1;                   // 伴奏 = 轨0(轨1/2 奇怪,不加)
                    // 伴奏洗净力度(vocalStrength 0~100;k=1=精确全洗"伴奏=原曲−人声",>1=过洗削伴奏,<1=留人声)
                    // 默认传 100 → k=1(标准);不再有滑条(已删),固定标准全洗。
                    float k = Math.Clamp(vocalStrength / 100f, 0f, 1.5f);
                    float vM = v;                        // 人声输出纯(不混原曲,分离就是要纯人声)
                    float accM = org - k * v;
                    if (target >= 100)
                    {
                        // 自定义组合:100+bitmask(1人声 2伴奏 4其他1 8其他2)——左右声道分别合成
                        int mask = target - 100;
                        float sum = 0;
                        if ((mask & 1) != 0) sum += vM;
                        if ((mask & 2) != 0) sum += accM;
                        if ((mask & 4) != 0) sum += o1;
                        if ((mask & 8) != 0) sum += o2;
                        sel[0, c, s] = sum;
                    }
                    else if (target == 6)
                    {
                        sel[0, c, s] = vM;                // 人声(轨3,强度混合)
                        sel[1, c, s] = accM;              // 伴奏(原曲−人声,干净)
                    }
                    else if (target == 7)
                    {
                        sel[0, c, s] = vM;                // 人声(轨3)
                        sel[1, c, s] = accM;              // 伴奏(原曲−人声)
                        sel[2, c, s] = o1;                // 其他1(轨1)
                        sel[3, c, s] = o2;                // 其他2(轨2)
                    }
                    else
                    {
                        sel[0, c, s] = target switch
                        {
                            0 => vM,                      // 人声(强度混合)
                            1 => accM,                    // 伴奏(原曲−人声)
                            2 => o1,                      // 其他1
                            3 => o2,                      // 其他2
                            4 => accM,                    // 伴奏(近似)
                            _ => vM + accM,               // 人声+伴奏(重混=近似原曲)
                        };
                    }
                }
            if (target == 6)
            {
                // 分离:输出 2 个文件(人声/伴奏,从 outputWav 派生文件名)
                var dir = System.IO.Path.GetDirectoryName(outputWav) ?? ".";
                var baseName = System.IO.Path.GetFileNameWithoutExtension(outputWav);
                var ext = System.IO.Path.GetExtension(outputWav);
                var vocals = System.IO.Path.Combine(dir, baseName + "_人声" + ext);
                var accomp = System.IO.Path.Combine(dir, baseName + "_伴奏" + ext);
                var v = new float[N_CHANNELS, total];
                var a2 = new float[N_CHANNELS, total];
                for (int c = 0; c < N_CHANNELS; c++)
                    for (int s = 0; s < total; s++)
                    {
                        v[c, s] = sel[0, c, s];
                        a2[c, s] = sel[1, c, s];
                    }
                WriteWav(vocals, v, total);
                WriteWav(accomp, a2, total);
            }
            else if (target == 7)
            {
                // 全轨:输出 4 个文件(人声/伴奏/其他1/其他2)——一次推理供"分离+升采样率"共用,免两次分轨
                var dir = System.IO.Path.GetDirectoryName(outputWav) ?? ".";
                var baseName = System.IO.Path.GetFileNameWithoutExtension(outputWav);
                var ext = System.IO.Path.GetExtension(outputWav);
                var names = new[] { "_人声", "_伴奏", "_其他1", "_其他2" };
                for (int st = 0; st < 4; st++)
                {
                    var path = System.IO.Path.Combine(dir, baseName + names[st] + ext);
                    var d = new float[N_CHANNELS, total];
                    for (int c = 0; c < N_CHANNELS; c++)
                        for (int s = 0; s < total; s++)
                            d[c, s] = sel[st, c, s];
                    WriteWav(path, d, total);
                }
            }
            else
            {
                var only = new float[N_CHANNELS, total];
                for (int c = 0; c < N_CHANNELS; c++)
                    for (int s = 0; s < total; s++)
                        only[c, s] = sel[0, c, s];
                WriteWav(outputWav, only, total);
            }
    }

    private static float[] MakeWindow(int n, int overlap)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = 1f;
        for (int i = 0; i < overlap; i++)
        {
            float f = (float)i / overlap;
            w[i] = f;
            w[n - 1 - i] = f;
        }
        return w;
    }

    /// <summary>读取 16-bit PCM WAV(44.1k 立体声),返回 (float[channels, samples], sampleCount)。</summary>
    private static (float[,], int) ReadWav(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        // RIFF 头
        string riff = new string(br.ReadChars(4));
        if (riff != "RIFF") throw new InvalidDataException("不是 WAV");
        br.ReadInt32();
        string wave = new string(br.ReadChars(4));
        int fmtChunk = 0; short audioFormat = 0, channels = 0; int sampleRate = 0, bits = 0;
        while (fs.Position < fs.Length && fmtChunk == 0)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                audioFormat = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                fmtChunk = 1;
                // 跳过剩余 fmt
                if (size > 16) fs.Seek(size - 16, SeekOrigin.Current);
            }
            else fs.Seek(size + (size % 2), SeekOrigin.Current);
        }
        while (true)
        {
            if (fs.Position + 8 > fs.Length) break;
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "data")
            {
                int bytesPerSample = bits / 8;
                int samples = size / (bytesPerSample * channels);
                var mix = new float[channels, samples];
                for (int s = 0; s < samples; s++)
                    for (int c = 0; c < channels; c++)
                        mix[c, s] = br.ReadInt16() / 32768f;
                // 立体声保护:Demucs 需双声道;单声道输入复制到双声道(避免越界崩溃)
                if (channels == 1)
                {
                    var stereo = new float[2, samples];
                    for (int s = 0; s < samples; s++) { stereo[0, s] = mix[0, s]; stereo[1, s] = mix[0, s]; }
                    return (stereo, samples);
                }
                return (mix, samples);
            }
            fs.Seek(size + (size % 2), SeekOrigin.Current);
        }
        throw new InvalidDataException("WAV 无 data 块");
    }

    /// <summary>写出 16-bit PCM WAV(立体声)。</summary>
    private static void WriteWav(string path, float[,] data, int samples)
    {
        int channels = data.GetLength(0);
        int dataSize = samples * channels * 2;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);            // PCM
        bw.Write((short)channels);
        bw.Write(SAMPLE_RATE);
        bw.Write(SAMPLE_RATE * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        for (int s = 0; s < samples; s++)
            for (int c = 0; c < channels; c++)
                bw.Write((short)(Math.Clamp(data[c, s], -1f, 1f) * 32767f));
    }
}
