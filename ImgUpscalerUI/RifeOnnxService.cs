// RifeOnnxService.cs — 补帧 ONNX 路线(为 50 系/无独显等 ncnn-Vulkan 不可用设备):
// 标准 RIFE v4.9 模型(输入 img0/img1/timestep),DirectML GPU 优先;偶发失败只对【本对帧】CPU 重算一次,
// 设备被摘除(887A)或连续失败达上限则不落 CPU(「补帧绝不落 CPU」是硬约定),由调用方复制原帧。
// 模型:engines/rife/rife49.onnx(20.5MB,MIT,社区 yuvraj108c/rife-onnx 导出)。
// 用途:VideoService 在 RIFE ncnn 引擎 GPU 探测失败时,优先走本 ONNX 路线(而非直接降 CPU)。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class RifeOnnxService
{
    // 会话按设备号缓存:首帧可能是小帧 CPU 会话,若单会话复用,后续大帧全落 CPU(慢 ~19 倍)。
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, InferenceSession> _sessions = new();
    static readonly object _sessionGate = new();

    /// <summary>会话 → 它【真实】所在的 DirectML 设备号(-1 = 这个会话其实是 CPU 会话)。
    /// 【为什么需要】调用方传进来的 gpuId 只表示"我想要 GPU";DML append 失败时建出来的其实是纯 CPU 会话,
    /// 若拿"CPU 推理成功"去 ClearDmlStrikes(该 GPU 设备号),设备级熔断就永远无法触发
    /// (DmlDeviceUnusable/AnyDmlDeviceUnusable 一直报健康,视频会持续重试那块死掉的卡)。
    /// 【为什么用 ConditionalWeakTable】它按【引用】比较键(与 Equals 重写无关),且会话被回收后条目自动消失,
    /// 不会像普通字典那样把已 Dispose 的会话钉在内存里。</summary>
    sealed class SessionDevice { public int Dml; public int Concurrency = 1; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InferenceSession, SessionDevice> _sessionDml = new();

    static void TagSession(InferenceSession session, int dmlDevice)
        => TagSession(session, dmlDevice, 1);

    /// <summary>把"这个会话真实所在设备"和"建它时声明的并发路数"一起钉在会话上。
    /// 并发路数必须在这里记:分块大小要按【显存 ÷ 路数】算,而 RunCore 只拿到一个会话,
    /// 拿不到调用方(VideoService)的并发数(见 CreateSessions)。</summary>
    static void TagSession(InferenceSession session, int dmlDevice, int concurrency)
        => _sessionDml.AddOrUpdate(session, new SessionDevice { Dml = dmlDevice, Concurrency = Math.Max(1, concurrency) });

    /// <summary>这个会话建的时候声明了几路并发(查不到按 1 路)。
    /// 用于把显存预算摊到每一路上:分块大小是"每路能占多少显存"的函数,不是整机显存。</summary>
    static int ConcurrencyOfSession(InferenceSession session)
        => _sessionDml.TryGetValue(session, out var tag) ? Math.Max(1, tag.Concurrency) : 1;

    /// <summary>这个会话【确实】跑在哪个 DirectML 设备上(-1 = CPU 会话)。查不到标志时按调用方的 gpuId 兜底
    /// (只可能是本类之外建的会话)。</summary>
    static int DmlOfSession(InferenceSession session, int gpuId)
        => _sessionDml.TryGetValue(session, out var tag) ? tag.Dml : (gpuId >= 0 ? gpuId : -1);

    /// <summary>ONNX 模型路径(engines/rife/rife49.onnx;不存在返回 null = 不启用 ONNX 路线)。</summary>
    public static string? FindModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "rife");
        var f = Path.Combine(root, "rife49.onnx");
        return File.Exists(f) ? f : null;
    }

    /// <summary>是否可走 ONNX 补帧路线(模型在才考虑;调用方还需 GPU 探测失败才真正用)。</summary>
    public static bool Available() => FindModel() != null;

    /// <summary>建一个 DirectML 会话(不缓存)。
    /// 【形参语义:dmlDevice 已经是 DirectML 设备号,不是引擎 -g 编号】——调用方(VideoService)已用
    /// EngineService.ResolveDmlDevice 解析过(那一步负责"尊重用户选择 + 无效编号兜底 + 锁定独显")。
    /// 【为什么这里绝不能再映射一次】历史事故:bc554b5 为了"锁定独显"在调用方和本函数【同时】加了映射,
    /// 而 EngineService.ResolveDmlDevice 并不幂等——ResolveEngineGpu 在编号存在于引擎表时原样返回
    /// (EngineService.cs 里那句"尊重用户选择(核显就核显)"),于是第二次调用会把【DML 号当引擎号】再解释一遍。
    /// 双卡机上引擎序与 DXGI 序相反(实测:引擎 [0 独显][1 核显] vs DXGI [#0 核显][#1 独显]),
    /// 两次映射正好互相抵消成"选独显 → 建到核显",而所有日志与设备号都显示独显。
    /// → 编号空间只允许解析一次:解析在调用方,这里直接用。dmlDevice&lt;0 表示调用方明确要 CPU。
    /// 失败规则与超分一致:持久设备错误(887A)重抛(不落 CPU),其它失败打明确日志并回退 CPU。
    /// <paramref name="onDml"/> 出参 = 这个会话【确实】建在 DirectML 上(见 _sessionDml 说明:C-4 要靠显式标志,
    /// 不靠 gpuId&gt;=0 推断);DML append 失败时它是 false,而返回的会话是纯 CPU 会话。</summary>
    static InferenceSession BuildSession(int dmlDevice, out bool onDml)
    {
        var opts = new SessionOptions();
        onDml = false;
        if (dmlDevice >= 0)
        {
            try
            {
                opts.AppendExecutionProvider_DML(dmlDevice);
                onDml = true;
            }
            catch (Exception dmlEx)
            {
                if (AlhPro.Core.GpuFault.IsPersistentDeviceError(dmlEx)) throw;   // 设备摘除:不落 CPU,交由调用方复制原帧
                // 【第 1 项】完整诊断(设备号/HRESULT 十六进制/异常类型/Message 首行/InnerException 链 + 定性):
                // 只留"原因:首行"时分不清是显存不足 0x8007000E、设备摘除 0x887A0005/6 还是 provider 注册失败。
                EsrganOnnxService.LogDmlFailure("RifeOnnxService.BuildSession.AppendExecutionProvider_DML", dmlDevice, dmlEx);
            }
        }
        // 【第 4 项①】没有 DML(= 本会话其实是 CPU 会话)→ 显式限制 ONNX CPU 线程数。
        // 默认(intra_op_num_threads=0)会让 ONNX 按【物理核】自建线程池,而该线程池在本进程内、
        // 不受 Job 对象 CPU 上限约束 → CPU 100% + 界面卡。GPU 会话不设(算子跑在设备上)。
        if (!onDml) EsrganOnnxService.ApplyConservativeCpuThreads(opts);
        return new InferenceSession(FindModel()!, opts);
    }

    /// <summary>共享会话缓存(dmlDevice = DirectML 设备号,见 BuildSession 说明)。</summary>
    static InferenceSession GetSession(int dmlDevice)
    {
        if (_sessions.TryGetValue(dmlDevice, out var s) && s != null) return s;
        lock (_sessionGate)
        {
            if (_sessions.TryGetValue(dmlDevice, out var s2) && s2 != null) return s2;
            var ses = BuildSession(dmlDevice, out bool onDml);
            // 【C-4 ②】DML 建不起来时会话其实是 CPU:绝不能缓存在 GPU 键 dmlDevice 下(同键后续全部命中它,
            // 而且它推理成功还会去清零该 GPU 的连击 → 设备级熔断永不触发)。改缓存在 CPU 键 -1,
            // GPU 键只留给【真 DML 会话】。
            int cacheKey = onDml ? dmlDevice : -1;
            if (_sessions.TryGetValue(cacheKey, out var existing) && existing != null)
            {
                ses.Dispose();   // 已有同键会话(例如上次失败时建的 CPU 会话):复用它,本次新建的别泄漏
                TagSession(existing, cacheKey);
                return existing;
            }
            _sessions[cacheKey] = ses;
            TagSession(ses, cacheKey);
            return ses;
        }
    }

    /// <summary>创建 concurrency 个独立 DirectML 会话(并行 worker 每个独占一个;绝不共用/并发 Run 同一会话,
    /// DirectML InferenceSession 非线程安全)。由调用方负责 finally 里 Dispose。
    /// 【concurrency 不只是循环次数】它同时决定每路能用多少显存(总预算 ÷ 路数),进而决定分块大小——
    /// 所以这里把并发数钉在会话上(见 TagSession/ConcurrencyOfSession),RunCore 才能按路数收紧分块。
    /// <paramref name="dmlDevice"/> 必须是【已解析的 DirectML 设备号】(见 BuildSession 说明,不要传引擎 -g 编号)。</summary>
    public static InferenceSession[] CreateSessions(int concurrency, int dmlDevice)
    {
        var arr = new InferenceSession[Math.Max(1, concurrency)];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = BuildSession(dmlDevice, out bool onDml);
            // 把"这个会话到底跑在 DML 上还是 CPU 上"钉在会话本身上:调用方(VideoService)只会把 dmlDevice
            // 传回来,没法区分这两者,而熔断/连击的写法必须靠这个标志(C-4 ④)。
            // 并发路数一并记上:N 路 = N 份推理工作集同时在显存里,分块必须按 1/N 的预算收紧。
            TagSession(arr[i], onDml ? dmlDevice : -1, arr.Length);
        }
        return arr;
    }

    /// <summary>用【指定会话】在 img0/img1 间插 time 帧,写入 outputPng。worker 用自己独占的会话调用,
    /// 不从共享 _sessions 取(否会同会话并发 Run 崩)。
    /// 【gpuId 必须传 DirectML 设备号】(与本类 BuildSession/连击表同一编号空间;引擎 -g 编号请先经
    /// EngineService.ResolveDmlDevice 解析)。它仅用于熔断判定与错误关联。</summary>
    public static void InterpWithSession(InferenceSession session, string img0, string img1, float time,
        string outputPng, int gpuId)
    {
        var model = FindModel() ?? throw new FileNotFoundException("缺少补帧模型:rife49.onnx");
        if (gpuId != -1 && EsrganOnnxService.DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止补帧尝试(不降级到慢速 CPU)。请重启软件后重试。");
        if (EsrganOnnxService.DmlDeviceUnusable(gpuId))
            throw new InvalidOperationException(
                $"GPU(DirectML 设备 {gpuId})连续多次补帧推理失败,本进程内视为不可用——已停止补帧尝试(不降级到慢速 CPU)。"
                + "剩余帧将复制原帧;请重启软件后重试。");
        RunCore(session, img0, img1, time, outputPng, gpuId, model);
    }

    /// <summary>用 ONNX 模型在 img0 与 img1 之间插 time(0~1) 帧,输出到 outputPng。gpuId&gt;=0 走 DirectML,
    /// 偶发失败只对本对帧 CPU 重算一次,设备级失效/连续失败则抛出(不落 CPU);
    /// gpuId=-2 表示自动(按输入尺寸:大帧 GPU/小帧 CPU,实测小帧 CPU 反而快 19 倍)。</summary>
    public static void Interp(string img0, string img1, float time, string outputPng, int gpuId = -1)
    {
        var model = FindModel() ?? throw new FileNotFoundException("缺少补帧模型:rife49.onnx");
        // 【设备已死 = 快速失败,绝不落 CPU】887A0005/887A0006 之后本进程的 D3D 设备已被摘除,每次 DirectML 调用
        // 必然失败;这种情况转 CPU 会把整段视频静默拖到 CPU 上补帧(几十分钟起步),违反「补帧绝不落 CPU」。
        // gpuId=-1 表示调用方明确要 CPU(本机无 GPU 可用)——那是唯一允许用 CPU 的场景,不在此列。
        if (gpuId != -1 && EsrganOnnxService.DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止补帧尝试(不降级到慢速 CPU)。请重启软件后重试。");
        // -2 = 自动选设备。PickDevice 返回的是【引擎 -g 编号】,而 BuildSession/GetSession/连击表
        // 统一用【DirectML 设备号】——所以在这里一次性解析,之后本方法内 gpuId 一律是 DML 号。
        if (gpuId == -2)
        {
            try
            {
                using (var probe = new System.Drawing.Bitmap(img0))
                    gpuId = EsrganOnnxService.PickDevice(probe.Width, probe.Height);
            }
            catch { gpuId = -1; }
            if (gpuId >= 0) gpuId = EngineService.ResolveDmlDevice(gpuId);
        }
        // 连续瞬时失败已达上限:快速失败。必须在 -2 解析【之后】查——自动路径传进来的是 -2,解析前查永远命中不了,
        // 于是每对帧都白试一次"建会话 + 注定失败的推理",那正是这个检查要省掉的成本。
        // 抛出后调用方按帧复制原帧(毫秒级)。原先这里把 gpuId 改成 -1,等于一次抖动就让剩下整段视频在 CPU 上补帧。
        if (EsrganOnnxService.DmlDeviceUnusable(gpuId))
            throw new InvalidOperationException(
                $"GPU(DirectML 设备 {gpuId})连续多次补帧推理失败,本进程内视为不可用——已停止补帧尝试(不降级到慢速 CPU)。"
                + "剩余帧将复制原帧;请重启软件后重试。");
        var session = GetSession(gpuId);
        RunCore(session, img0, img1, time, outputPng, gpuId, model);
    }

    static void RunCore(InferenceSession session, string img0, string img1, float time, string outputPng,
        int gpuId, string model)
    {
        using var bmp0 = LoadBitmap(img0);
        using var bmp1 = LoadBitmap(img1);
        if (bmp0.Width != bmp1.Width || bmp0.Height != bmp1.Height)
            throw new InvalidOperationException("两帧尺寸不一致,无法补帧");

        int w = bmp0.Width, h = bmp0.Height;
        // 【C-4 ④】这个会话【确实】跑在 DirectML 上吗?由建会话时钉在会话上的标志决定,绝不用 gpuId>=0 推断:
        // gpuId 只表示调用方"要 GPU",而 DML append 失败时会话其实是纯 CPU 会话 —— 拿这种会话的成功去
        // ClearDmlStrikes(该 GPU),等于每次 CPU 成功都把 GPU 的死活洗白,设备级熔断永远无法触发。
        int dmDevice = DmlOfSession(session, gpuId);
        bool dmlSession = dmDevice >= 0;
        // 分块大小按【显存 ÷ 并发路数】自适应:0 = 整帧(优先),>0 = 分块边长。理由与实测数据见 ResolveInterpTile。
        // CPU 会话不受显存墙约束 → 保持整帧(与旧行为一致,不引入新的失败面)。
        int tile = dmlSession ? ResolveInterpTile(w, h, ConcurrencyOfSession(session)) : 0;
        try
        {
            if (tile > 0)
            {
                RunTiled(session, bmp0, bmp1, time, outputPng, w, h, tile, TileMargin);
                EsrganOnnxService.ClearDmlStrikes(dmDevice);   // GPU 真跑成功 → 偶发抖动不该累积
                return;
            }
            RunSingle(session, bmp0, bmp1, time, outputPng, w, h, dmDevice);
            if (dmlSession) EsrganOnnxService.ClearDmlStrikes(dmDevice);
        }
        catch (Exception ex) when (dmlSession)
        {
            // 设备被摘除/挂死(887A):本进程内不可恢复 → 熔断并抛出,让调用方按帧复制原帧(毫秒级)。
            // CPU 整帧重算等于整段视频在 CPU 上补帧(几十分钟起步),违反「补帧绝不落 CPU」。
            if (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex))
            {
                EsrganOnnxService.TripDmlDead(dmDevice, ex);
                throw;
            }
            // 偶发失败:本对帧换 CPU 重算一次(代价有界,绝不出黑帧/半帧)。但连击达上限就认定设备不可用 → 抛出,
            // 由调用方复制原帧。原先这里写 _dmlBad[gpuId] 永久闩锁,一次抖动就让剩下整段视频都在 CPU 上补帧。
            if (EsrganOnnxService.NoteDmlTransientFailure(dmDevice))
                throw new InvalidOperationException(
                    $"RIFE ONNX 补帧失败:GPU(DirectML 设备 {dmDevice})连续多次推理失败,已停止尝试(不降级到慢速 CPU)。"
                    + "剩余帧将复制原帧;请重启软件后重试。", ex);
            AppLogger.Warn($"RIFE ONNX DirectML 失败,本对帧改 CPU 整帧重算: {ex.Message.Split('\n')[0]}");
            DropSession(dmDevice);
            RunSingle(GetSession(-1), bmp0, bmp1, time, outputPng, w, h, -1);
        }
    }

    /// <summary>整帧推理(CPU 或小帧 GPU)。GPU 失败一律抛出(设备级失效就地熔断),恢复策略由 RunCore 统一决定。
    /// <paramref name="dmDevice"/> = 该会话【真实】所在的 DirectML 设备号(-1 = CPU 会话;CPU 会话失败不写任何 GPU 连击表)。</summary>
    static void RunSingle(InferenceSession session, Bitmap bmp0, Bitmap bmp1, float time, string outputPng,
        int w, int h, int dmDevice)
    {
        // 【修复 补帧没效果】RIFE ONNX 输入要求宽高为 4 的倍数。原整帧路径直接把 w/h 喂进模型,
        // 非 4 倍数的帧会抛形状错误 → 上层 catch 后静默复制左端点 → 该帧不插帧(看起来"没效果")。
        // 与分块路径一致:先补到 4 的倍数(用边缘像素复制),推理后再裁回原尺寸。
        int pw = (w + 3) & ~3, ph = (h + 3) & ~3;
        bool padded = pw != w || ph != h;
        var t0 = padded ? ToTensorRect(bmp0, 0, 0, pw, ph) : ToTensor(bmp0);
        var t1 = padded ? ToTensorRect(bmp1, 0, 0, pw, ph) : ToTensor(bmp1);
        var tensor0 = new DenseTensor<float>(t0, new[] { 1, 3, ph, pw });
        var tensor1 = new DenseTensor<float>(t1, new[] { 1, 3, ph, pw });
        var ts = new DenseTensor<float>(new[] { time }, new[] { 1 });

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            // 【第 4 项②】CPU 会话(dmDevice<0)的推理进"本进程内 CPU 计算"作用域:
            // 让 SafeRender 的 CPU 硬上限压到 CpuComputeCapPct(65%)。GPU 会话不进作用域,上限维持原值。
            using var cpuScope = dmDevice < 0 ? SafeRender.EnterOwnCpuCompute() : null;
            results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("img0", tensor0),
                NamedOnnxValue.CreateFromTensor("img1", tensor1),
                NamedOnnxValue.CreateFromTensor("timestep", ts),
            });
        }
        catch (Exception ex) when (dmDevice >= 0)
        {
            // 设备被摘除/挂死(887A):就地熔断(越早置位,越多调用点能立刻快速失败),然后抛出。
            if (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex)) EsrganOnnxService.TripDmlDead(dmDevice, ex);
            // 恢复策略统一在 RunCore:它才知道该复制原帧还是 CPU 重算一次。原先这里自己转 CPU 重算,失败后异常
            // 传到 RunCore 又转一次 —— 同一对帧做了两次 CPU 推理,白等一倍时间。
            throw;
        }
        using (results)
        {
            var outTensor = results!.First().AsTensor<float>();
            var dims = outTensor.Dimensions;
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidOperationException($"ONNX 输出形状异常: {string.Join("x", dims.ToArray())}");
            int oh = dims[2], ow = dims[3];
            if (oh <= 0 || ow <= 0 || oh > 16384 || ow > 16384)
                throw new InvalidOperationException($"ONNX 输出尺寸异常({ow}x{oh}),模型输出异常,无法落图");
            var pixels = new float[3 * oh * ow];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = outTensor.GetValue(i);
            using var full = FromTensor(pixels, ow, oh);
            if (padded && ow >= w && oh >= h)
            {
                using var cropped = full.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format24bppRgb);
                SavePng(cropped, outputPng);
            }
            else
                SavePng(full, outputPng);
        }
    }

    /// <summary>分块边长下限(像素)。再小就没有意义:块越小 Run 次数越多(每次都有固定的 DirectML 往返+张量拷贝开销),
    /// 而显存收益已经趋平(实测 512 块在 4K 下峰值也只有 0.36GB,再往下省不出多少)。</summary>
    const int MinTile = 256;

    /// <summary>分块时的边缘余量(像素):每块向四周各多取这么多行/列当上下文,写回时丢弃。
    /// 16 是实测值:现行 512 分块 + 16px 余量下,真实素材上块边界处的"逐列相邻差分"与整帧基准之比只有
    /// 0.45~1.14 倍(没有尖峰);只有强空间变化运动(zoom 106%)才会露出边界台阶(见 ResolveInterpTile)。
    /// 所以 16 足够压住边界,不需要为此加大(加大 = 块更贵、更慢)。</summary>
    const int TileMargin = 16;

    // ---------- 整帧/分块的显存模型:常量全部由本机取证实测标定,不是拍脑袋的档位 ----------
    // 取证环境:RTX 4060 Laptop 8G(TotalVramGB=8.0 / EffectiveVramGB=6.0)、DirectML、rife49.onnx、
    // 同一对 1920×1080 真实帧、同一 time=0.5。每条路【独立进程】测(否则 DirectML 分配器池会把上一路的
    // 峰值留在显存里把数字抬高),报「nvidia-smi 峰值总占用 / 相对干净基线的增量」+ 预热后多次取均值耗时:
    //   1080p(2.07MP)  整帧      480 ms/帧  Δ1439MiB(总 3168MiB)
    //                  分块 1024  854 ms/帧  Δ1161MiB(总 2890MiB)
    //                  分块 512  1078 ms/帧   Δ357MiB(总 2086MiB)
    //   4K   (8.29MP)  整帧     1676 ms/帧  Δ5392MiB(总 7120MiB —— 8188MiB 的卡只剩 1GB)
    //                  分块 2048 2898 ms/帧  Δ5226MiB(总 6120MiB)
    //                  分块 1024 2985 ms/帧  Δ1160MiB(总 2908MiB)
    //                  分块 512  3483 ms/帧   Δ365MiB(总 2093MiB)
    // 三个关键结论(决定了下面的策略):
    // ① 【整帧明显更快】1080p 整帧 480ms/帧 vs 512 分块 1078~1405ms/帧(快 2.2~2.9 倍):分块把每次 Run 的
    //    固定开销(DirectML 往返 + 张量拷贝)付了 12 遍以上。原先"GPU 大帧一律 512 分块"在这台机器上是纯亏。
    // ② 【每百万像素的峰值,整帧反而更省】整帧 650~695 MiB/MP,分块路径 1075~1230 MiB/MP(边距让每块的
    //    输入面积都放大,而固定开销按块重复)。但整帧的总面积大,所以绝对峰值仍可能超过某个块——
    //    能不能整帧必须按预算算(见下面 ResolveInterpTile),不能凭"整帧更小"想当然。
    // ③ 【分块必须保留】4K 整帧峰值总占用 7120/8188MiB(只剩 1GB 余量),再叠上并发路数/浏览器就很可能爆显存;
    //    爆显存 → 设备摘除(887A0005/6)是【本进程不可恢复】的故障,比慢严重得多。4K 改走 1024 分块后总占用只有 2908MiB。
    const double TileFixedMiB = 400;         // 会话/模型/临时缓冲的固定开销(按实测上界取)
    const double WholeMiBPerMp = 900;        // 整帧:每百万像素峰值(实测 650~695,放大留余量)
    const double TiledMiBPerMp = 1400;       // 分块:每百万像素峰值(实测 1075~1230,放大留余量)
    const double VramSafety = 0.75;          // 再留 25%:估不准时宁可取更小的块(慢),绝不为提速冒爆显存的风险

    /// <summary>本帧该不该分块、用多大的块:返回 0 = 整帧(不分块),否则为分块边长(像素)。
    /// 【为什么整帧优先】见上面一段的实测:整帧在 1080p 上比 512 分块快 2~3 倍、每百万像素峰值还更低,
    /// 而且没有分块边界(分块 = 每块各自独立估光流,边界处两条独立估计相接;本次取证在 zoom 106% 的强空间
    /// 变化运动上,实测 512 分块的 x=1536 处逐列相邻差分是整帧基准的 3.32 倍、y=1024 处逐行差分 15.47 倍,
    /// 换成 1024 分块后 x=1536 的边界消失、该处降到 0.51 倍。真实素材/纯平移下 16px 边距把边界压到
    /// 0.45~1.14 倍(看不出接缝),所以边距维持 16 不改,能不分块就不分块)。
    /// 【并发路数算进预算】补帧是多会话并发,每个 worker 独占一个会话、各自持有一份推理工作集,
    /// 所以预算是「(显存预算 ÷ 路数)×安全系数」,而路数由 CreateSessions 建会话时钉在会话上(TagSession)。
    /// 【为什么不会爆显存】① 预算口径直接复用 SafeRender(自动档 = 本机总量 75%,自定义档 = 用户设的上限),
    /// 不再另造一套阈值;并且只有【实测到】空闲显存时才用它收紧(AMD/Intel 上那是估算值,SafeRender 的注释
    /// 明确警告过不能拿它当判据)。② 每百万像素的峰值常量比实测值还放大 1.3~1.9 倍,再乘 0.75 安全系数
    /// (本机 1080p:估计 2263MiB vs 实测 1439MiB;4K:估计 7861MiB vs 实测 5392MiB)。
    /// ③ 它只用来决定"能不能放大":估不准一律取更小的块,最小到 MinTile;连 MinTile 都算不下时仍返回 MinTile
    /// (它是最省显存的一档,且分块峰值随块面积近似线性下降,块小永远比块大安全)。
    /// <paramref name="concurrency"/> = 并发的会话路数(1 = 单路/共享会话)。</summary>
    internal static int ResolveInterpTile(int w, int h, int concurrency)
    {
        double vramGb;
        try
        {
            vramGb = SafeRender.EffectiveVramGB;
            if (SafeRender.FreeVramMeasured) vramGb = Math.Min(vramGb, SafeRender.FreeVramGB);
        }
        catch
        {
            return 512;   // 显存探测异常:退回原来的固定分块(保守),绝不因为"探测失败"就中断补帧
        }
        double budgetMiB = vramGb * 1024.0 / Math.Max(1, concurrency) * VramSafety;

        // ① 先试整帧:放得下就用整帧(最快,而且没有分块边界)
        if (TileFixedMiB + WholeMiBPerMp * (w * (double)h / 1e6) <= budgetMiB) return 0;

        // ② 整帧放不下:从大到小挑第一个放得下的块。面积按"块边长 + 两侧边距"(RunTiled 实际喂给模型的尺寸)
        //    估算;块边长必须是 4 的倍数(RIFE 输入要求),候选全是 4 的倍数。
        for (int t = 2048; t >= MinTile; t /= 2)
        {
            double side = t + 2.0 * TileMargin;
            if (TileFixedMiB + TiledMiBPerMp * (side * side / 1e6) <= budgetMiB) return t;
        }
        return MinTile;
    }

    /// <summary>分块插帧:tile×tile 块 + 边缘余量(margin)防接缝;块尺寸取 4 的倍数(RIFE 输入要求),越界用边缘像素填充。
    /// 【块大小不再写死 512】由 <see cref="ResolveInterpTile"/> 按显存 ÷ 并发路数决定(tile 由调用方传入)。</summary>
    static void RunTiled(InferenceSession session, Bitmap bmp0, Bitmap bmp1, float time, string outputPng,
        int w, int h, int tile, int margin)
    {
        using var outBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = outBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                for (int ty = 0; ty < h; ty += tile)
                {
                    for (int tx = 0; tx < w; tx += tile)
                    {
                        int tw = Math.Min(tile, w - tx);
                        int th = Math.Min(tile, h - ty);
                        int sx0 = Math.Max(0, tx - margin), sy0 = Math.Max(0, ty - margin);
                        int bw = Math.Min(w, tx + tw + margin) - sx0;
                        int bh = Math.Min(h, ty + th + margin) - sy0;
                        int pw = (bw + 3) & ~3, ph = (bh + 3) & ~3;   // 向上取 4 的倍数
                        var t0 = ToTensorRect(bmp0, sx0, sy0, pw, ph);
                        var t1 = ToTensorRect(bmp1, sx0, sy0, pw, ph);
                        var tensor0 = new DenseTensor<float>(t0, new[] { 1, 3, ph, pw });
                        var tensor1 = new DenseTensor<float>(t1, new[] { 1, 3, ph, pw });
                        var ts = new DenseTensor<float>(new[] { time }, new[] { 1 });
                        using var results = session.Run(new[]
                        {
                            NamedOnnxValue.CreateFromTensor("img0", tensor0),
                            NamedOnnxValue.CreateFromTensor("img1", tensor1),
                            NamedOnnxValue.CreateFromTensor("timestep", ts),
                        });
                        var outT = results.First().AsTensor<float>();
                        // 【输出形状校验,必须在写像素之前】与 RunSingle 同款,但 RunTiled 原先没有:
                        // 模型输出异常时原来的代码会在下面的像素循环里抛 IndexOutOfRangeException(或写进错位像素),
                        // 那是个"看不出所以然"的异常 —— 调用方只能看到下标越界,拿不到块坐标/期望尺寸,无从下手。
                        // 这里提前校验维数与高宽,给出可行动的上下文(期望 vs 实际 + 块坐标 + 输入尺寸)。
                        var dims = outT.Dimensions;
                        if (dims.Length != 4 || dims[1] != 3)
                            throw new InvalidOperationException(
                                $"RIFE 分块输出形状异常: {string.Join("x", dims.ToArray())}"
                                + $"(期望 1x3x{ph}x{pw};块 tx={tx},ty={ty},输入 {pw}×{ph})");
                        int oh = dims[2], ow = dims[3];
                        // 只写块有效中心区(丢弃边缘余量,防接缝)
                        int ox = tx - sx0, oy = ty - sy0;
                        if (ow < ox + tw || oh < oy + th)
                            throw new InvalidOperationException(
                                $"RIFE 分块输出尺寸不足: 实际 {ow}×{oh},本块至少需要 {ox + tw}×{oy + th}"
                                + $"(块 tx={tx},ty={ty},输入 {pw}×{ph},边距 {margin})");
                        for (int yy = 0; yy < th; yy++)
                        {
                            byte* row = ptr + (ty + yy) * data.Stride + tx * 3;
                            for (int xx = 0; xx < tw; xx++)
                            {
                                float r = outT[0, 0, oy + yy, ox + xx];
                                float g2 = outT[0, 1, oy + yy, ox + xx];
                                float b2 = outT[0, 2, oy + yy, ox + xx];
                                row[xx * 3] = (byte)Math.Clamp((int)Math.Round(b2 * 255f), 0, 255);
                                row[xx * 3 + 1] = (byte)Math.Clamp((int)Math.Round(g2 * 255f), 0, 255);
                                row[xx * 3 + 2] = (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255);
                            }
                        }
                    }
                }
            }
        }
        finally { outBmp.UnlockBits(data); }
        SavePng(outBmp, outputPng);
    }

    /// <summary>按区域读像素(偏移 + 越界边缘填充;写满 pw×ph 张量,供 RIFE 分块使用)。</summary>
    static float[] ToTensorRect(Bitmap src, int ox, int oy, int pw, int ph)
    {
        int w = src.Width, h = src.Height;
        var pixels = new float[3 * ph * pw];
        var rect = new Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0.ToPointer();
                int plane = pw * ph;
                for (int yy = 0; yy < ph; yy++)
                {
                    int sy = Math.Min(oy + yy, h - 1);
                    for (int xx = 0; xx < pw; xx++)
                    {
                        int sx = Math.Min(ox + xx, w - 1);
                        byte* px = basePtr + sy * data.Stride + sx * 3;
                        int idx = yy * pw + xx;
                        pixels[idx] = px[2] / 255f;               // R
                        pixels[plane + idx] = px[1] / 255f;      // G
                        pixels[plane * 2 + idx] = px[0] / 255f;  // B
                    }
                }
            }
        }
        finally { src.UnlockBits(data); }
        return pixels;
    }

    static void DropSession(int gpuId)
    {
        if (_sessions.TryRemove(gpuId, out var old)) old?.Dispose();
    }

    // ---------- System.Drawing 工具(与电脑版 EsrganOnnxService 一致) ----------

    static Bitmap LoadBitmap(string path)
    {
        Bitmap probe;
        try { probe = new Bitmap(path); }
        catch (Exception ex) { throw new InvalidOperationException($"补帧输入帧无法解码:{Path.GetFileName(path)}——{ex.Message}", ex); }
        using (probe)
        {
            if (probe.Width <= 0 || probe.Height <= 0)
                throw new InvalidOperationException($"补帧输入帧尺寸异常(0×0):{Path.GetFileName(path)}");
            // 复制为 24bpp(保证 LockBits 像素格式稳定;System.Drawing 读取的默认格式可移植性差)
            var dst = new Bitmap(probe.Width, probe.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(dst);
            g.DrawImage(probe, 0, 0, probe.Width, probe.Height);
            return dst;
        }
    }

    static float[] ToTensor(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        var pixels = new float[3 * h * w];
        var rect = new Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int plane = w * h;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        byte* px = basePtr + y * data.Stride + x * 3;
                        pixels[idx] = px[2] / 255f;               // R
                        pixels[plane + idx] = px[1] / 255f;      // G
                        pixels[plane * 2 + idx] = px[0] / 255f;  // B
                    }
            }
        }
        finally { src.UnlockBits(data); }
        return pixels;
    }

    static Bitmap FromTensor(float[] t, int w, int h)
    {
        // 数值防御(源头不黑):模型输出若含 NaN/Inf(数值溢出/除0)→ 该帧已损坏,直接抛出让调用方回退源帧,
        // 避免 (int)NaN=0 → 全黑帧 写进输出。正常帧无 NaN,仅一次数组扫描,开销远小于推理。
        for (int i = 0; i < t.Length; i++)
            if (float.IsNaN(t[i]) || float.IsInfinity(t[i]))
                throw new InvalidOperationException("ONNX 输出含 NaN/Inf(数值异常),该帧补帧结果无效");
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int plane = w * h;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        byte* px = basePtr + y * data.Stride + x * 3;
                        px[0] = (byte)Math.Clamp((int)Math.Round(t[plane * 2 + idx] * 255f), 0, 255);
                        px[1] = (byte)Math.Clamp((int)Math.Round(t[plane + idx] * 255f), 0, 255);
                        px[2] = (byte)Math.Clamp((int)Math.Round(t[idx] * 255f), 0, 255);
                    }
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    static void SavePng(Bitmap bmp, string path)
    {
        bmp.Save(path, ImageFormat.Png);
    }
}
