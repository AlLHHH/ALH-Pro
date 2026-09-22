using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// "这个 (模型, 设备) 组合还值不值得再试 DirectML" 的账本单测。
///
/// 【为什么要有这个账本】实测(2026-09-22,RTX 4060 Laptop,见 _qa\视频抠图_性能实测_20260922.md):
/// <c>birefnet-lite</c>(输入 1024²)在本机 DirectML 上【每一次】推理都失败
/// (DmlFusedNode 图融合 8007000E → DmlCommandRecorder 80004005),而它恰好是抠图页的默认模型。
/// 失败点在 session.Run 而不是建会话,于是每一次调用都先把注定失败的 DML 尝试走一遍再回退 CPU:
/// 实测每帧白费约 1.4 秒(6816 ms vs 纯 CPU 5395 ms)。图片页是"每张图慢 1.4 秒",
/// 视频页就是"1800 帧 × 1.4 秒 ≈ 白烧 42 分钟"。所以必须记住"这个组合上 DML 已经废了"。
///
/// 【为什么按 (模型, 设备) 而不是只按设备】同一台机器上 <c>isnet-general-use</c> 的 DirectML 是好的
/// (406 ms/帧,比 CPU 快 2.6 倍)。按设备一刀切会把一个好路径一起关掉。
///
/// 【为什么连吃 N 次才认定,而不是错一次就关】偶发抖动(别人占用显存、临时 OOM)不该累积成永久结论;
/// 判错的代价不对称:多试两次最多白花几秒,而一次误判会让整个进程(视频就是整段)失去 2.6 倍加速。
/// </summary>
public class DmlAttemptLedgerTests
{
    private const string BirefnetLite = @"D:\engines\rembg\birefnet-lite.onnx";
    private const string Isnet = @"D:\engines\rembg\isnet-general-use.onnx";

    [Fact]
    public void Fresh_ledger_allows_gpu()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 3);
        Assert.True(ledger.ShouldAttempt(BirefnetLite, 0, deviceDead: false));
        Assert.Equal(0, ledger.ConsecutiveFailures(BirefnetLite, 0));
        Assert.False(ledger.IsBroken(BirefnetLite, 0));
    }

    /// <summary>连吃满上限才停:前两次仍给机会(偶发抖动),第三次之后不再白试。</summary>
    [Fact]
    public void Stops_attempting_after_the_strike_limit()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 3);

        Assert.False(ledger.NoteFailure(BirefnetLite, 0));   // 第 1 次:还没到上限
        Assert.True(ledger.ShouldAttempt(BirefnetLite, 0, deviceDead: false));

        Assert.False(ledger.NoteFailure(BirefnetLite, 0));   // 第 2 次
        Assert.True(ledger.ShouldAttempt(BirefnetLite, 0, deviceDead: false));

        Assert.True(ledger.NoteFailure(BirefnetLite, 0));    // 第 3 次:达上限
        Assert.False(ledger.ShouldAttempt(BirefnetLite, 0, deviceDead: false));
        Assert.True(ledger.IsBroken(BirefnetLite, 0));
    }

    /// <summary>中间成功一次就清零:不能把"偶发失败 + 成功 + 偶发失败"累积成设备不可用。</summary>
    [Fact]
    public void Success_resets_the_counter()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 3);
        ledger.NoteFailure(BirefnetLite, 0);
        ledger.NoteFailure(BirefnetLite, 0);

        ledger.NoteSuccess(BirefnetLite, 0);

        Assert.Equal(0, ledger.ConsecutiveFailures(BirefnetLite, 0));
        Assert.False(ledger.NoteFailure(BirefnetLite, 0));   // 重新从第 1 次开始
        Assert.True(ledger.ShouldAttempt(BirefnetLite, 0, deviceDead: false));
    }

    /// <summary>实测现场:先跑 birefnet-lite 连败 3 次,紧随其后的 isnet 必须照旧能用 GPU(它本身是好的)。</summary>
    [Fact]
    public void One_broken_combo_does_not_disable_another()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 3);
        for (int i = 0; i < 3; i++) ledger.NoteFailure(BirefnetLite, 0);

        Assert.False(ledger.ShouldAttempt(BirefnetLite, 0, deviceDead: false));
        Assert.True(ledger.ShouldAttempt(Isnet, 0, deviceDead: false));      // 另一支模型不受牵连
        Assert.Equal(0, ledger.ConsecutiveFailures(Isnet, 0));
    }

    /// <summary>同一支模型换设备(0 号卡 / 1 号卡)各自记账:核显上失败不该关掉独显。</summary>
    [Fact]
    public void Counters_are_per_device()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 2);
        ledger.NoteFailure(Isnet, 0);
        ledger.NoteFailure(Isnet, 0);

        Assert.False(ledger.ShouldAttempt(Isnet, 0, deviceDead: false));
        Assert.True(ledger.ShouldAttempt(Isnet, 1, deviceDead: false));
    }

    /// <summary>设备已被摘除/挂死(EsrganOnnxService 的进程级熔断)时,任何组合都别再试。</summary>
    [Fact]
    public void Dead_device_blocks_every_combo()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 3);
        Assert.False(ledger.ShouldAttempt(Isnet, 0, deviceDead: true));
        Assert.False(ledger.ShouldAttempt(BirefnetLite, 1, deviceDead: true));
    }

    /// <summary>用户主动选 CPU(gpuId &lt; 0)时不该被账本"拦"成 CPU —— 本来就是 CPU,由调用方决定,不在这里判。</summary>
    [Fact]
    public void Negative_device_is_not_this_ledgers_business()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 1);
        // 账本只回答"还想试 GPU 吗";gpuId<0 的语义由调用方短路(见 CutoutService)。
        Assert.True(ledger.ShouldAttempt(Isnet, -1, deviceDead: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Strike_limit_must_be_positive(int limit)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new DmlAttemptLedger(limit));
    }

    /// <summary>路径大小写不同不该被当成两个组合(Windows 路径不区分大小写)。</summary>
    [Fact]
    public void Path_comparison_ignores_case()
    {
        var ledger = new DmlAttemptLedger(strikeLimit: 1);
        ledger.NoteFailure(BirefnetLite, 0);

        Assert.False(ledger.ShouldAttempt(BirefnetLite.ToUpperInvariant(), 0, deviceDead: false));
    }
}
