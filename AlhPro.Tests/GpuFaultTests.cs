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
}
