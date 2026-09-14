using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>ONNX 显存策略的单测(F3):并行会话路数按可用显存动态定 + 分块真降档阶梯 + "显存不足"的异常分类。
/// 这几条决定了"8GB 卡会不会因为开 2 路 OOM"以及"OOM 后是降块重试还是直接把该帧判死" ——
/// 真机事故:8GB 卡(显存墙 6.0GB)按旧口径开 2 路 → E_OUTOFMEMORY → 逐帧回退源帧(超分等于没放大)。</summary>
public class OnnxVramPolicyTests
{
    // ---------- 并行会话路数 ----------

    [Fact]
    public void Concurrency_gpu_off_is_always_one()
    {
        Assert.Equal(1, RenderPolicy.OnnxSessionConcurrency(false, 24.0, null));
        Assert.Equal(1, RenderPolicy.OnnxSessionConcurrency(false, 24.0, 24.0));
    }

    [Fact]
    public void Concurrency_8gb_laptop_card_falls_back_to_one_route()
    {
        // 真机(RTX 5060 Laptop 8GB):有效显存墙 = 8×0.75 = 6.0GB → 1 路(旧口径这里是 2 路,正是 OOM 成因)
        Assert.Equal(1, RenderPolicy.OnnxSessionConcurrency(true, 6.0, null));
        // 空闲显存实测 5.5GB(桌面/浏览器占着)→ 仍是 1 路
        Assert.Equal(1, RenderPolicy.OnnxSessionConcurrency(true, 6.0, 5.5));
    }

    [Fact]
    public void Concurrency_two_routes_from_8gb_budget()
    {
        Assert.Equal(2, RenderPolicy.OnnxSessionConcurrency(true, 8.0, null));
        Assert.Equal(2, RenderPolicy.OnnxSessionConcurrency(true, 9.0, null));       // 12GB 卡的墙 9.0
        Assert.Equal(2, RenderPolicy.OnnxSessionConcurrency(true, 16.0, 8.0));       // 空闲实测更小 → 听空闲的
    }

    [Fact]
    public void Concurrency_three_routes_only_on_12gb_budget()
    {
        Assert.Equal(3, RenderPolicy.OnnxSessionConcurrency(true, 12.0, null));
        Assert.Equal(3, RenderPolicy.OnnxSessionConcurrency(true, 12.0, 12.0));
        Assert.Equal(3, RenderPolicy.OnnxSessionConcurrency(true, 18.0, 15.0));
    }

    [Fact]
    public void Concurrency_ignores_unmeasured_free_vram()
    {
        // 0/负数 = "没实测到"(SafeRender 在 AMD/Intel 上给的是估算值,不能当判据)→ 只按有效显存判
        Assert.Equal(3, RenderPolicy.OnnxSessionConcurrency(true, 12.0, 0.0));
        Assert.Equal(3, RenderPolicy.OnnxSessionConcurrency(true, 12.0, -1.0));
    }

    [Fact]
    public void ConcurrencyRule_explains_basis_for_log()
    {
        string withFree = RenderPolicy.OnnxConcurrencyRule(true, 6.0, 5.0);
        Assert.Contains("有效显存 6.0GB", withFree);
        Assert.Contains("5.0GB(已实测)", withFree);
        Assert.Contains("预算 5.0GB", withFree);
        Assert.Contains("≥8GB→2 路", withFree);

        string noFree = RenderPolicy.OnnxConcurrencyRule(true, 6.0, null);
        Assert.Contains("未实测(不参与判定)", noFree);
        Assert.Contains("预算 6.0GB", noFree);

        Assert.Contains("CPU", RenderPolicy.OnnxConcurrencyRule(false, 6.0, null));
    }

    [Fact]
    public void Concurrency_is_monotonic_in_budget()
    {
        // 预算越大路数不会变少(策略单调,避免"显存越多反而越慢")
        int prev = 0;
        foreach (double b in new[] { 2.0, 4.0, 6.0, 8.0, 10.0, 12.0, 16.0, 24.0 })
        {
            int c = RenderPolicy.OnnxSessionConcurrency(true, b, null);
            Assert.True(c >= prev, $"预算 {b}GB 的路数 {c} 不应小于更小预算的 {prev}");
            prev = c;
        }
    }

    // ---------- 分块真降档阶梯 ----------

    [Fact]
    public void TileLadder_from_auto_1024_steps_down_to_min()
    {
        Assert.Equal(new[] { 1024, 512, 256, 128 }, RenderPolicy.OnnxTileLadder(1024));
    }

    [Fact]
    public void TileLadder_skips_steps_not_smaller_than_current()
    {
        Assert.Equal(new[] { 512, 256, 128 }, RenderPolicy.OnnxTileLadder(512));
        Assert.Equal(new[] { 384, 256, 128 }, RenderPolicy.OnnxTileLadder(384));
        Assert.Equal(new[] { 256, 128 }, RenderPolicy.OnnxTileLadder(256));
        Assert.Equal(new[] { 128 }, RenderPolicy.OnnxTileLadder(128));
        Assert.Equal(new[] { 64 }, RenderPolicy.OnnxTileLadder(64));   // 已经比最小档还小:不重复、不放大
    }

    [Fact]
    public void TileLadder_is_strictly_decreasing_and_ends_at_or_below_min()
    {
        foreach (int auto in new[] { 64, 100, 128, 200, 512, 768, 1024, 2048 })
        {
            var lad = RenderPolicy.OnnxTileLadder(auto);
            Assert.Equal(auto, lad[0]);                                  // 首元素 = 调用方算出来的自动档
            for (int i = 1; i < lad.Length; i++)
                Assert.True(lad[i] < lad[i - 1], $"必须严格递减:{auto} → {string.Join(",", lad)}");
            Assert.True(lad[^1] <= RenderPolicy.MinOnnxTile || lad.Length == 1, $"阶梯末档应落到最小档:{auto}");
        }
    }

    [Fact]
    public void TileLadder_uses_same_steps_as_ncnn_path()
    {
        // 与 ncnn 路径(512→256→128)同口径,日志才好对照
        Assert.Equal(new[] { 512, 256, 128 }, RenderPolicy.OnnxTileLadderSteps);
        Assert.Equal(128, RenderPolicy.MinOnnxTile);
    }

    // ---------- "显存不足"的异常分类 ----------

    [Fact]
    public void VramShortage_detects_hresult_e_outofmemory()
    {
        Assert.True(GpuFault.IsVramShortage(new HResultException(unchecked((int)0x8007000E))));
    }

    [Theory]
    [InlineData("0x8007000E")]
    [InlineData("E_OUTOFMEMORY")]
    [InlineData("Failed to allocate memory for tensor")]
    [InlineData("Out of memory while allocating")]
    [InlineData("not enough memory resources are available")]
    [InlineData("DmlExecutionProvider: insufficient memory")]
    [InlineData("显存不足")]
    [InlineData("内存不足")]
    public void VramShortage_detects_vendor_and_runtime_wording(string message)
    {
        Assert.True(GpuFault.IsVramShortage(new Exception(message)));
    }

    [Fact]
    public void VramShortage_finds_root_cause_in_inner_exception()
    {
        // 真机形态:外层是"ONNX 超分失败(并行会话): …",真正的 OOM 在内层
        var wrapped = new InvalidOperationException("ONNX 超分失败(并行会话): ONNX 超分失败",
            new Exception("", new HResultException(unchecked((int)0x8007000E))));
        Assert.True(GpuFault.IsVramShortage(wrapped));
    }

    [Fact]
    public void VramShortage_is_mutually_exclusive_with_persistent_device_error()
    {
        // 设备被摘除时绝不能被判成"显存不足":否则会拿一次不可恢复故障去逐级降档重试(白等),
        // 而正确处置是触发熔断。即使文案里带 alloc 字样也必须判 false。
        var removed = new HResultException(unchecked((int)0x887A0005), "device removed: failed to allocate");
        Assert.True(GpuFault.IsPersistentDeviceError(removed));
        Assert.False(GpuFault.IsVramShortage(removed));
    }

    [Fact]
    public void VramShortage_ignores_unrelated_failures()
    {
        Assert.False(GpuFault.IsVramShortage(new Exception("ONNX 输出形状异常: 1x3x0x0")));
        Assert.False(GpuFault.IsVramShortage(new OperationCanceledException("The operation was canceled.")));
        Assert.False(GpuFault.IsVramShortage(new HResultException(unchecked((int)0x80004005))));
    }

    /// <summary>与 GpuFaultTests 同款的测试用异常(HResult 的 setter 是 protected,只有派生类能造)。</summary>
    private sealed class HResultException : Exception
    {
        public HResultException(int hresult, string message = "") : base(message) { HResult = hresult; }
    }
}
