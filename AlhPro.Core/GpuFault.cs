namespace AlhPro.Core;

/// <summary>
/// GPU 设备错误分类(纯字符串逻辑,可单测)。设备状态本身(DirectML 熔断标志、会话缓存)留在
/// EsrganOnnxService/RifeOnnxService,这里只回答一个问题:这个异常是不是"设备已被摘除/挂死"。
/// 判错方向的代价极不对称,所以宁可宽判:
///   漏判(把持续性错误当偶发)→ 调用方会逐帧重试必然失败的 GPU 调用,并在每次失败后新建 CPU 会话重算,
///     一段视频就是几小时(用户看到的是"设置里写着 GPU、实际跑了一整夜")。
///   误判(把偶发错误当持续性)→ 该批次回退成源帧缩放,几十秒出片,画面偏软但完整,且日志里有一条明确警告。
/// 因此这里把厂商/运行时可能出现的各种写法(错误码、枚举名、小写变体、文件名、中文本地化)全部收进来。
/// </summary>
public static class GpuFault
{
    /// <summary>
    /// 是否为【持续性】GPU 设备错误:DXGI_ERROR_DEVICE_REMOVED(887A0005)/DEVICE_HUNG(887A0006)/
    /// DEVICE_RESET(887A0007)/DRIVER_INTERNAL_ERROR(887A0020)。这类错误一旦发生,该进程的 D3D 设备即被
    /// Windows 摘除,后续每一次 DirectML 调用都必然失败——重试(无论 GPU 还是 CPU)都救不回来,只有重启软件才行。
    /// 与之相对的是单帧/单块的偶发错误(尺寸不合规、显存瞬时不足),那些仍值得重试一次。
    /// 遍历 InnerException 链:ONNX Runtime 常把 DXGI 错误包在两三层里面。
    /// </summary>
    public static bool IsPersistentDeviceError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            var s = e.Message;
            if (string.IsNullOrEmpty(s)) continue;
            if (s.Contains("887A0005", StringComparison.OrdinalIgnoreCase)
                || s.Contains("887A0006", StringComparison.OrdinalIgnoreCase)
                || s.Contains("887A0007", StringComparison.OrdinalIgnoreCase)
                || s.Contains("887A0020", StringComparison.OrdinalIgnoreCase)
                || s.Contains("DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
                || s.Contains("DEVICE_HUNG", StringComparison.OrdinalIgnoreCase)
                || s.Contains("device removed", StringComparison.OrdinalIgnoreCase)
                || s.Contains("device hung", StringComparison.OrdinalIgnoreCase)
                || s.Contains("DmlCommandRecorder", StringComparison.OrdinalIgnoreCase)
                || s.Contains("GPU 设备响应已停止", StringComparison.Ordinal)
                || s.Contains("设备已移除", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
