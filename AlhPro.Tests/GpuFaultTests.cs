using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// GPU 设备错误分类的单测。这个判定决定了"是立刻熔断回退源帧"还是"逐帧重试",判错的代价极不对称:
/// 漏判 → 一段视频逐帧重演"注定失败的 GPU 尝试 + 新建 CPU 会话 + CPU 推理",用户等几小时拿到一条黑视频
/// (真机诊断包里 240 帧跑几小时、4 个线程反复报 887A 就是这个形状);
/// 误判 → 该批次回退成源帧缩放,几十秒出片,画面偏软但完整。
/// 而触发条件(显卡被系统摘除)没法按需复现,所以识别逻辑只能靠单测守住。
/// </summary>
public class GpuFaultTests
{
    // ---- 必须命中:DXGI 设备级错误码 ----

    [Theory]
    [InlineData("Error 887A0005 occurred")]                              // DEVICE_REMOVED
    [InlineData("Error 887A0006 occurred")]                              // DEVICE_HUNG
    [InlineData("Error 887A0007 occurred")]                              // DEVICE_RESET
    [InlineData("Error 887A0020 occurred")]                              // DRIVER_INTERNAL_ERROR
    [InlineData("error 887a0005 occurred")]                              // 小写十六进制(OrdinalIgnoreCase)
    [InlineData("HRESULT 0x887A0006")]
    public void Dxgi_device_error_codes_are_persistent(string message)
    {
        Assert.True(GpuFault.IsPersistentDeviceError(new System.Exception(message)));
    }

    // ---- 必须命中:枚举名与本地化文案(不同运行时/驱动的写法不一样) ----

    [Theory]
    [InlineData("DXGI_ERROR_DEVICE_REMOVED")]
    [InlineData("DXGI_ERROR_DEVICE_HUNG")]
    [InlineData("dxgi_error_device_removed")]
    [InlineData("The device removed")]                                   // 小写变体单独列了一条匹配规则
    [InlineData("The device hung")]
    [InlineData("GPU 设备响应已停止")]                                    // Windows 中文本地化
    [InlineData("设备已移除")]
    public void Device_state_wording_is_persistent(string message)
    {
        Assert.True(GpuFault.IsPersistentDeviceError(new System.Exception(message)));
    }

    /// <summary>ONNX Runtime 的 DirectML EP 在设备挂死时常只报出源文件名,不带任何错误码。</summary>
    [Theory]
    [InlineData("DmlCommandRecorder::Close: unexpected error")]
    [InlineData("dmlcommandrecorder failed")]
    public void OnnxRuntime_dml_source_location_is_persistent(string message)
    {
        Assert.True(GpuFault.IsPersistentDeviceError(new System.Exception(message)));
    }

    /// <summary>真机诊断包里的原文形状:文件名 + 错误码 + 中文本地化挤在同一条消息里。</summary>
    [Fact]
    public void Real_world_diagnostic_message_is_persistent()
    {
        const string real = "DmlCommandRecorder.cpp:1234 (0x887A0006:GPU 设备响应已停止)";
        Assert.True(GpuFault.IsPersistentDeviceError(new System.Exception(real)));
    }

    // ---- 必须命中:被包在内层的错误 ----

    /// <summary>ONNX Runtime 常把 DXGI 错误包在两三层里面,只看最外层会漏判。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Nested_inner_exception_is_still_detected(int depth)
    {
        System.Exception ex = new System.Exception("Error 887A0005");
        for (int i = 0; i < depth; i++)
            ex = new System.Exception($"wrapper {i}", ex);

        Assert.True(GpuFault.IsPersistentDeviceError(ex));
    }

    /// <summary>内层消息为空时不能崩,且要继续往里层看。</summary>
    [Fact]
    public void Empty_message_in_chain_does_not_stop_the_walk()
    {
        var ex = new System.Exception("outer",
            new System.Exception("",
                new System.Exception("DXGI_ERROR_DEVICE_HUNG")));

        Assert.True(GpuFault.IsPersistentDeviceError(ex));
    }

    // ---- 必须【不】命中:偶发/可恢复错误 ----
    // 误判这些会让一张好卡被永久放弃(本进程内),用户只看到"画质变软",日志里查不出原因。

    [Theory]
    [InlineData("显存不足,无法分配 512x512 张量")]                        // 瞬时 OOM:重试/缩块就能过
    [InlineData("ONNX 输出形状异常: 1x3x0x0")]                            // 模型/尺寸问题,不是设备问题
    [InlineData("两帧尺寸不一致,无法补帧")]
    [InlineData("Invalid argument: input width must be a multiple of 4")]
    [InlineData("缺少超分模型:RealESRGAN_x4plus.onnx。")]
    [InlineData("The device is not ready")]                              // 含 "device" 但不是设备级失效
    [InlineData("Error 887A0001 occurred")]                              // DXGI_ERROR_INVALID_CALL:调用错误,可恢复
    [InlineData("Error 887A0002 occurred")]                              // DXGI_ERROR_WAS_STILL_DRAWING
    [InlineData("")]
    public void Transient_or_unrelated_errors_are_not_persistent(string message)
    {
        Assert.False(GpuFault.IsPersistentDeviceError(new System.Exception(message)));
    }

    /// <summary>取消不是设备错误:被判成持续性会让"用户点停止"变成"GPU 已损坏"的误导性提示。</summary>
    [Fact]
    public void Cancellation_is_not_persistent()
    {
        Assert.False(GpuFault.IsPersistentDeviceError(
            new System.OperationCanceledException("The operation was canceled.")));
    }

    /// <summary>外层是设备无关的包装、内层才是偶发错误时,同样不该命中。</summary>
    [Fact]
    public void Nested_transient_error_is_not_persistent()
    {
        var ex = new System.Exception("ONNX 超分失败(并行会话)",
            new System.Exception("显存不足"));

        Assert.False(GpuFault.IsPersistentDeviceError(ex));
    }

    // ---- 必须命中:D3D12/DirectML 设备挂起的最高频原文(本文件补充) ----
    // 这一条是全类里漏判代价最大的一条:D3D12 运行时的标准文案里【不含】0x887A0005
    // (HRESULT 要调用方事后自己调 GetDeviceRemovedReason 才拿得到),所以修前它不命中任何规则 →
    // 调用方逐帧重试必然失败的 GPU 调用、每次失败还新建 CPU 会话,一段视频几小时。

    [Theory]
    [InlineData("The GPU device instance has been suspended. Use GetDeviceRemovedReason to determine the appropriate action.")]
    [InlineData("the gpu device instance has been suspended")]                 // 大小写变体(OrdinalIgnoreCase)
    [InlineData("The GPU device instance has been suspended.")]                // 只到前半句(拼接方式可能不同)
    [InlineData("Use GetDeviceRemovedReason to determine the appropriate action.")]   // 只到后半句
    [InlineData("GetDeviceRemovedReason failed")]
    public void D3d12_device_suspended_wording_is_persistent(string message)
    {
        Assert.True(GpuFault.IsPersistentDeviceError(new System.Exception(message)));
    }

    /// <summary>同一条原文被 ONNX Runtime 又包了一层(真实路径的形状:包装层消息里没有关键词)。</summary>
    [Fact]
    public void Nested_d3d12_device_suspended_is_persistent()
    {
        var ex = new System.Exception("ONNX 补帧失败(会话 1)",
            new System.Exception("The GPU device instance has been suspended. " +
                                 "Use GetDeviceRemovedReason to determine the appropriate action."));

        Assert.True(GpuFault.IsPersistentDeviceError(ex));
    }

    // ---- 必须命中:按 HResult 数值判(COM/OnnxRuntime 内层异常最可靠的一条线索) ----

    /// <summary>这三条断言各钉一个要点:① 最外层就是设备错误;② 设备错误在 InnerException 上(最常见);
    /// ③ 该层 Message 为空时也必须命中 —— 修前 `string.IsNullOrEmpty(s)` 的 continue 会把这一层整条跳过,
    /// 而"只有 HResult、没有可读消息"恰恰是 COM 通道最常见的形状。</summary>
    [Theory]
    [InlineData(unchecked((int)0x887A0005))]  // DXGI_ERROR_DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0006))]  // DXGI_ERROR_DEVICE_HUNG
    [InlineData(unchecked((int)0x887A0007))]  // DXGI_ERROR_DEVICE_RESET
    [InlineData(unchecked((int)0x887A0020))]  // DXGI_ERROR_DRIVER_INTERNAL_ERROR
    public void Device_error_hresult_is_persistent(int hresult)
    {
        Assert.True(GpuFault.IsPersistentDeviceError(new HResultException(hresult)));

        Assert.True(GpuFault.IsPersistentDeviceError(
            new System.Exception("ONNX 推理失败", new HResultException(hresult))));

        Assert.True(GpuFault.IsPersistentDeviceError(
            new System.Exception("", new HResultException(hresult))));
    }

    /// <summary>真实形状:设备错误走 COM 通道(COMException 的 HRESULT 由构造器写入),消息里可以完全没有错误码。</summary>
    [Fact]
    public void ComException_with_device_hresult_is_persistent()
    {
        var ex = new System.Runtime.InteropServices.COMException(
            "GPU 调用失败", unchecked((int)0x887A0006));

        Assert.True(GpuFault.IsPersistentDeviceError(ex));
    }

    // ---- 必须【不】命中(反例比正例更重要:证明"按数值宽判"没有滥杀) ----

    /// <summary>同样是 887A 前缀、同样提到 device,但都不是设备级失效:这些一旦被判成持续性,
    /// 一张健康的卡会在本进程内被永久放弃,用户只看到"画质变软"而日志里查不出原因。</summary>
    [Theory]
    [InlineData("Error 887A0001 occurred")]        // DXGI_ERROR_INVALID_CALL:调用错误,可恢复
    [InlineData("Error 887A0002 occurred")]        // DXGI_ERROR_WAS_STILL_DRAWING:稍后重试即可
    [InlineData("The device is not ready")]        // 含 "device" 但不是设备级失效
    [InlineData("设备未就绪")]
    public void Transient_codes_and_wording_are_still_not_persistent(string message)
    {
        Assert.False(GpuFault.IsPersistentDeviceError(new System.Exception(message)));
    }

    /// <summary>取消 + 内层瞬态错误 + 【非设备级】HResult:证明数值判是白名单,不是"带 HResult 就算"。</summary>
    [Fact]
    public void Cancellation_and_non_device_hresults_are_not_persistent()
    {
        Assert.False(GpuFault.IsPersistentDeviceError(
            new System.OperationCanceledException("The operation was canceled.")));

        Assert.False(GpuFault.IsPersistentDeviceError(
            new System.Exception("ONNX 超分失败", new System.Exception("显存不足"))));

        // 887A0001 / 887A0002:同族码,但可恢复
        Assert.False(GpuFault.IsPersistentDeviceError(new HResultException(unchecked((int)0x887A0001))));
        Assert.False(GpuFault.IsPersistentDeviceError(new HResultException(unchecked((int)0x887A0002))));
        // E_OUTOFMEMORY:瞬态(重试/缩块能过);E_FAIL:通用失败
        Assert.False(GpuFault.IsPersistentDeviceError(new HResultException(unchecked((int)0x8007000E))));
        Assert.False(GpuFault.IsPersistentDeviceError(new HResultException(unchecked((int)0x80004005))));
        // 0x80131500 = 普通 .NET 异常的默认 HResult:不能因为"有 HResult"就判持续性
        Assert.False(GpuFault.IsPersistentDeviceError(new HResultException(unchecked((int)0x80131500))));
    }

    /// <summary>测试用异常:Exception.HResult 的 setter 是 protected,只有派生类能造出
    /// "文本与 HRESULT 不一致"的异常 —— 而真实链路上 HRESULT 恰恰只存在于这一层(消息可能是空的)。</summary>
    private sealed class HResultException : System.Exception
    {
        public HResultException(int hresult, string message = "") : base(message) { HResult = hresult; }
    }
}
