namespace AlhPro.Core;

/// <summary>
/// 「(模型, 设备) 这个组合还值不值得再试 DirectML」的进程内账本。
///
/// 【为什么必须有】实测(2026-09-22,RTX 4060 Laptop,见 _qa\视频抠图_性能实测_20260922.md):
/// `birefnet-lite`(输入 1024²)在本机 DirectML 上每一次推理都失败
/// (图融合 8007000E → DmlCommandRecorder 80004005),而它正是抠图页的默认模型;
/// 失败点在 session.Run 而非建会话,所以每次调用都要先走一遍注定失败的 DML 尝试再回退 CPU ——
/// 实测每帧白费约 1.4 秒(带 DML 失败的 6816 ms vs 纯 CPU 5395 ms)。
/// 图片页是"每张图慢 1.4 秒",视频页是"1800 帧 × 1.4 秒 ≈ 白烧 42 分钟"。
///
/// 【为什么键是 (模型, 设备) 而不是只按设备】同一台机器上 `isnet-general-use` 的 DirectML 是好的
/// (406 ms/帧,比 CPU 快 2.6 倍)。按设备一刀切会把这条好路径一起关掉 —— 所以账本必须细到组合。
///
/// 【为什么连吃 N 次才认定】偶发抖动(别的程序临时占显存)不该累积成永久结论。判错的代价不对称:
/// 多试两次最多白花几秒;一次误判会让整个进程(视频就是整段)丢掉 2.6 倍加速。
/// 上限由调用方传入(与 EsrganOnnxService 的 DmlTransientStrikes 同源),这里不写死第二个数字。
///
/// 纯内存状态机、无 ONNX/显卡依赖 ⇒ 可单测 —— 触发条件在特定显卡上,没法按需复现,只能靠单测钉住规则。
/// </summary>
public sealed class DmlAttemptLedger
{
    private readonly int _strikeLimit;

    /// <summary>键 = 模型路径 | 设备号。Windows 路径不区分大小写,故用 OrdinalIgnoreCase。</summary>
    private readonly Dictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <param name="strikeLimit">连吃多少次失败即认定该组合在本进程内不可用(须 ≥1)。</param>
    public DmlAttemptLedger(int strikeLimit)
    {
        if (strikeLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(strikeLimit), strikeLimit, "连击上限必须 ≥1");
        _strikeLimit = strikeLimit;
    }

    /// <summary>连击上限(供日志/界面显示"已连续 N/M 次失败")。</summary>
    public int StrikeLimit => _strikeLimit;

    private static string Key(string modelPath, int gpuId) => modelPath + "|" + gpuId.ToString();

    /// <summary>该组合是否已被判定"本进程内不要再试 DirectML"。</summary>
    public bool IsBroken(string modelPath, int gpuId)
    {
        lock (_gate) return _strikes.TryGetValue(Key(modelPath, gpuId), out var n) && n >= _strikeLimit;
    }

    /// <summary>当前连续失败次数(成功即清零)。</summary>
    public int ConsecutiveFailures(string modelPath, int gpuId)
    {
        lock (_gate) return _strikes.TryGetValue(Key(modelPath, gpuId), out var n) ? n : 0;
    }

    /// <summary>这次该不该尝试 DirectML。
    /// <paramref name="deviceDead"/>=设备级熔断(已摘除/挂死)时任何组合都不试;
    /// <paramref name="gpuId"/>&lt;0 = 调用方本来就选了 CPU,不归本账本管,原样放行。</summary>
    public bool ShouldAttempt(string modelPath, int gpuId, bool deviceDead)
    {
        if (gpuId < 0) return true;
        if (deviceDead) return false;
        return !IsBroken(modelPath, gpuId);
    }

    /// <summary>记一次 DirectML 失败。返回 true = 本次达到上限,该组合从此跳过(调用方应只提示一次)。</summary>
    public bool NoteFailure(string modelPath, int gpuId)
    {
        if (gpuId < 0) return false;
        lock (_gate)
        {
            var key = Key(modelPath, gpuId);
            int n = (_strikes.TryGetValue(key, out var old) ? old : 0) + 1;
            _strikes[key] = n;
            return n >= _strikeLimit;
        }
    }

    /// <summary>DirectML 推理成功 → 清零该组合的连击(偶发抖动不该累积成"不可用")。</summary>
    public void NoteSuccess(string modelPath, int gpuId)
    {
        if (gpuId < 0) return;
        lock (_gate) _strikes.Remove(Key(modelPath, gpuId));
    }
}
