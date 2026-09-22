using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>「边产边喂」的合帧通道:一个常驻 ffmpeg 进程,从标准输入接收 JPEG 序列(image2pipe),
/// 一边产帧一边解码+后处理+编码,从而与上游(补帧/超分,显卡在忙)重叠,而不是等全部产完再合帧。
///
/// 【为什么需要它】实测(_qa\bench_pipeline_overlap):真补帧+真超分下重叠可省 **19.5~23.7%** 墙钟,
/// 且成片**逐像素完全相同**(PSNR=inf);现状是"超分全部跑完 → 才开始合帧",合帧那段时间显卡完全闲着。
///
/// 【三条实测撞出来的硬约束,本类逐条兜住】
///   ① **必须等引擎"关闭"文件**才算写完:共享读会在引擎还在写时读到半张图 ——
///      实测那样喂进去,成片 PSNR 只有 14 dB(画面全错)。判据:FileShare.None 独占打开成功。
///   ② 帧必须**按最终顺序**喂:所以尾部若干帧要"押后"(见 HoldBackFrames),
///      等帧数对齐/尾帧容积定稿后再补喂,否则被删掉的帧已经喂进去、被补的帧又没喂,帧数就错了。
///   ③ 机器状态会漂 ⇒ 任何异常都不许"猜",一律让调用方**退回文件式合帧重做**。
///
/// 【不做的事】不判断"该不该用我"(由调用方给出资格判定)、不负责快启动(+faststart 无法边写边加,
/// 见调用方注释)、不碰界面。</summary>
public sealed class StreamMux : IDisposable
{
    /// <summary>喂帧线程里待写的字节队列(生产者=流水线,消费者=喂帧线程)。</summary>
    private readonly Queue<byte[]> _queue = new();
    private readonly object _gate = new();

    private readonly Process _proc;
    private readonly Stream _stdin;
    private readonly Thread _feeder;
    private readonly StringBuilder _err = new();

    private bool _producerDone;      // 生产侧已经不再新增
    private bool _feederDone;        // 喂帧线程已把队列写完并关闭 stdin
    private Exception? _feedError;   // 喂帧过程中的异常(必须让调用方看到,不许静默)
    private int _fedFrames;

    /// <summary>已喂入的帧数(用于与最终帧数对账)。</summary>
    public int FedFrames => Volatile.Read(ref _fedFrames);
    /// <summary>喂帧线程是否已收工(stdin 已关闭)。</summary>
    public bool FeederDone => _feederDone;
    /// <summary>喂帧线程里的异常;非空表示这条通道不可信,调用方应退回文件式合帧。</summary>
    public Exception? FeedError => _feedError;

    /// <summary>输出文件(临时名,由调用方负责校验后改名)。</summary>
    public string OutputPath { get; }

    private StreamMux(Process proc, string outputPath)
    {
        _proc = proc;
        OutputPath = outputPath;
        _stdin = proc.StandardInput.BaseStream;
        _feeder = new Thread(FeederLoop) { IsBackground = true, Name = "alh-stream-mux-feeder" };
        _feeder.Start();
    }

    /// <summary>启动常驻 ffmpeg。
    /// <param name="ffmpegExe">用哪个 ffmpeg(必须与实际合帧同一个二进制,探测结论才对得上)。</param>
    /// <param name="muxArgsBeforeInput">输入之前的所有参数(如 "-nostats -progress pipe:1")。</param>
    /// <param name="muxArgsAfterInput">输入之后的所有参数(滤镜/编码/音频/映射/输出前的参数)。</param>
    /// <param name="frameRate">序列帧率(与文件式合帧的 -framerate 必须一致)。</param>
    /// <param name="outputPath">写到哪个文件(建议 .tmp 名,校验通过后再改名为正式名)。</param>
    public static StreamMux Start(string ffmpegExe, string muxArgsBeforeInput, string muxArgsAfterInput,
        double frameRate, string outputPath, string workingDir)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string fr = frameRate.ToString("0.######", inv);
        string args = $"{muxArgsBeforeInput} -f image2pipe -framerate {fr} -i pipe:0 {muxArgsAfterInput}";

        var psi = new ProcessStartInfo(ffmpegExe, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,   // -progress pipe:1 会写 stdout,必须排空,否则管道堵死
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动合帧 ffmpeg(边产边喂)");
        var mux = new StreamMux(proc, outputPath);
        // stdout/stderr 必须持续排空:否则 ffmpeg 写满了就卡住(表现为"编码不动了")
        _ = Task.Run(() => Drain(proc.StandardOutput, null));
        _ = Task.Run(() => Drain(proc.StandardError, mux._err));
        return mux;
    }

    private static void Drain(StreamReader r, StringBuilder? sink)
    {
        try
        {
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (sink == null) continue;
                lock (sink)
                {
                    sink.AppendLine(line);
                    // 只留尾巴,防止长任务把内存吃掉
                    if (sink.Length > 64 * 1024) sink.Remove(0, sink.Length - 32 * 1024);
                }
            }
        }
        catch { /* 进程结束/管道关闭:正常路径 */ }
    }

    /// <summary>把一帧(文件路径)排进喂帧队列。**会等引擎放开这个文件**(见类注释约束①)。
    /// 注意:本方法不做磁盘以外的等待,队列积压由喂帧线程消化 —— 生产侧不会被编码拖住。</summary>
    public void FeedFile(string path, CancellationToken ct = default)
    {
        byte[]? bytes = ReadWhenClosed(path, TimeSpan.FromSeconds(60), ct);
        if (bytes == null) throw new IOException($"等不到引擎放开帧文件(或它一直为空):{Path.GetFileName(path)}");
        lock (_gate)
        {
            _queue.Enqueue(bytes);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>把一帧(已在内存里的字节)排进队列 —— 用于"末帧定格补帧"这类重复帧,不必再落盘。</summary>
    public void FeedBytes(byte[] bytes)
    {
        lock (_gate) { _queue.Enqueue(bytes); Monitor.Pulse(_gate); }
    }

    /// <summary>等引擎放开文件后整帧读入(= 该帧已写完)。超时/被取消返回 null。</summary>
    internal static byte[]? ReadWhenClosed(string path, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None:能独占打开 = 写入方已放手 = 这一帧完整(约束①)
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (fs.Length == 0) { Thread.Sleep(15); continue; }
                using var ms = new MemoryStream((int)fs.Length);
                fs.CopyTo(ms);
                if (ms.Length == 0) { Thread.Sleep(15); continue; }
                return ms.ToArray();
            }
            catch (IOException) { Thread.Sleep(15); }
            catch (UnauthorizedAccessException) { Thread.Sleep(15); }
        }
        return null;
    }

    private void FeederLoop()
    {
        try
        {
            while (true)
            {
                byte[]? item = null;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_producerDone) Monitor.Wait(_gate, 50);
                    if (_queue.Count > 0) item = _queue.Dequeue();
                    else if (_producerDone) break;
                }
                if (item == null) continue;
                _stdin.Write(item, 0, item.Length);
                _stdin.Flush();
                Interlocked.Increment(ref _fedFrames);
            }
            _stdin.Close();   // EOF → ffmpeg 收尾并写完文件
        }
        catch (Exception ex)
        {
            _feedError = ex;   // 不许吞:调用方要据此退回文件式合帧
            try { _stdin.Close(); } catch { }
        }
        finally { _feederDone = true; }
    }

    /// <summary>生产侧声明"不再有帧了",并等待 ffmpeg 把文件写完。返回 (退出码, stderr 尾巴)。
    /// 超时(默认 30 分钟)会杀掉进程并返回 (-1, 说明)。</summary>
    public async Task<(int code, string err)> FinishAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        lock (_gate) { _producerDone = true; Monitor.Pulse(_gate); }
        _feeder.Join(TimeSpan.FromMinutes(5));
        var limit = timeout ?? TimeSpan.FromMinutes(30);
        var sw = Stopwatch.StartNew();
        while (!_proc.HasExited && sw.Elapsed < limit)
        {
            if (ct.IsCancellationRequested) { try { _proc.Kill(true); } catch { } break; }
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
        }
        int code;
        if (!_proc.HasExited)
        {
            try { _proc.Kill(true); } catch { }
            code = -1;
        }
        else code = _proc.ExitCode;
        await Task.Run(() => _proc.WaitForExit(5000)).ConfigureAwait(false);
        string tail;
        lock (_err) tail = _err.ToString();
        return (code, tail);
    }

    public void Dispose()
    {
        lock (_gate) { _producerDone = true; Monitor.Pulse(_gate); }
        try { if (!_proc.HasExited) _proc.Kill(true); } catch { }
        try { _proc.Dispose(); } catch { }
    }
}
