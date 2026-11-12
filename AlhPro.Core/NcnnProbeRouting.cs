// 探测结论 → 该不该改用 ONNX(2026-11-12)
// 【为什么单独抽出来】EngineService.EnsureNcnnProbeAsync 返回的是 bool:实测不可用、空闲显存不足跳过、
// 本会话已跳过、被取消 —— 四种情况都返回 false。调用点若拿 bool 当"能不能用 ncnn",就会把"没测"当成"测出不行"。
// 真机事故(诊断包 ALHPro_Diag_20261112_1854,RTX 5060 Ti 8GB,启动时空闲显存只剩 0.2GB):
// 生产尺寸探测被显存闸跳过 ⇒ bool=false ⇒ 整段超分从 ncnn-Vulkan 切到 ONNX DirectML,
// 实测 6 秒/帧(同卡 ncnn 约 0.1~0.3 秒/帧),而这张卡的 ncnn 其实是好的(同机 RIFE 探测通过)。
// 规则只有一条:只有“跑完并判不可用”才换路;没测通(未得出结论)就继续用 ncnn,靠运行期防线兜底。
namespace AlhPro.Core
{
    /// <summary>三态探测结论(与 <see cref="NcnnProbeReport"/> 同义,这里只用它做路由判定)。</summary>
    public static class NcnnProbeRouting
    {
        /// <summary>结论是否要求改用 ONNX 稳定引擎。<b>只有确定性失败</b>才换路。</summary>
        public static bool WantsOnnxFallback(NcnnProbeReport decision)
            => decision == NcnnProbeReport.Unavailable;

        /// <summary>结论是否允许继续走 ncnn-Vulkan(通过 或 没测通都允许)。</summary>
        public static bool KeepsNcnn(NcnnProbeReport decision)
            => !WantsOnnxFallback(decision);

        /// <summary>把"启动自检挑卡"那一路的旧 bool 结论(它只有可用/不可用两态)转成三态:
        /// true → Available,false → Unavailable。<b>不要</b>把"没测"传成 false。</summary>
        public static NcnnProbeReport FromBoolean(bool usable)
            => usable ? NcnnProbeReport.Available : NcnnProbeReport.Unavailable;
    }
}
