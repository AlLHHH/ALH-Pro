// VideoView.Route.cs — 超分/补帧路线判定与预计耗时(从 VideoView.xaml.cs 拆出)
// 【为什么拆】原文件一万三千行;路线判定(会不会落 CPU、是不是备用路线)是改得最勤的一块。

using System;
using Microsoft.UI.Xaml;

namespace ALHPro.Views;

public sealed partial class VideoView
{
    /// <summary>本次超分是否会落到 CPU。只判【确定的】场景,不猜:
    /// ① 用户显式选了 CPU(AppSettings.GpuIndex &lt; 0);
    /// ② 本次超分走 ONNX 路线,而 DirectML 已【实测完成且确认不可用】
    ///    (DmlProbeCompleted 且 DmlFallbackOk &lt; 0)—— ONNX 路线下 DML 建会话失败就会静默落到 CPU,
    ///    这正是真机诊断包里"8 秒/帧"的成因。
    /// 【为什么必须先看 DmlProbeCompleted】DmlFallbackOk==-1 有两种含义:还没探测(未知)/ 探完确认不可用。
    /// 把"未知"当"不可用"会误报"要用 CPU"吓用户,所以只在探测【完成并判定不可用】时才提示。
    /// ncnn-Vulkan 路线不受 DirectML 影响,不提示。</summary>
    private bool UpscaleWillFallbackToCpu()
    {
        try
        {
            if (UpscaleToggle.IsChecked != true) return false;
            // 【2026-11-14】① 只在 DirectML 也不可用时才算"会落 CPU":设置页没有 CPU 选项,
            // GpuIndex<0 是自检自动降级的结果;若 DML 还能建会话,纹理会走 ONNX GPU,不该吓用户。
            if (AppSettings.GpuIndex < 0 && !ALHPro.VulkanCheck.DmlAvailable()) return true;
            if (!ALHPro.EsrganOnnxService.DmlProbeCompleted || ALHPro.EsrganOnnxService.DmlFallbackOk >= 0)
                return false;                            // 未探完 / 探测说可用 → 不当作 CPU 场景(不误报)
            return OnnxUpscaleRouteLikely();             // ② 走 ONNX 路线 + DirectML 实测不可用
        }
        catch { return false; }
    }

    /// <summary>当前下拉选中的补帧模型名(引擎侧用名)。与 RunBtn 构造参数时用的是同一个映射,
    /// 抽出来是为了让"兼容提示"和"实际执行"永不脱钩(两处各写一份迟早分叉)。</summary>
    private string SelectedInterpModel => InterpModelCombo.SelectedIndex switch
    {
        0 => "rife-v4.13",
        1 => "rife-v4.6",
        // 【2026-09-24 末尾追加第 3 支】权重已随包(发布版\engines\rife\rife-v4.26),
        // 用的是同一份 rife-ncnn-vulkan-2026.exe,实测 1080p 79~92 ms/输出帧。
        2 => "rife-v4.26",
        // 【2026-09-16 下拉精简 / 2026-09-24 改为三支】动漫/高清/超高清/经典兼容 已下架;老设置里存的
        // **3~6** 会被读设置时的范围检查(ReadSettings 里的 d.Model < Items.Count)挡掉 ⇒ 自动落到默认的 0(v4.13)。
        // 注意:存的 **2** 现在落进合法范围,会被解释成 v4.26(09-16 之前 2 是「动漫专用」)—— 见 XAML 那段注释的说明。
        // 这里保留兜底,是为了越界时也绝不返回一个不存在的模型目录名。
        _ => "rife-v4.13",
    };

    /// <summary>本次超分是否会走 ONNX 路线(只用 EngineService 的公共判定函数,不复制 VideoService 内部状态):
    /// 兼容模式强制 ONNX;其余一律【以真机实测结论为准】——没测过时不按显卡型号断言(与 ShouldUseOnnx* 同口径)。
    /// 判定不出(引擎未枚举等)返回 false —— 宁可漏提示,也不误报"要用 CPU"。</summary>
    private bool OnnxUpscaleRouteLikely()
    {
        try
        {
            if (FastModeCheck.IsChecked == true) return true;   // 兼容模式:ncnn 路径同样改走 ONNX
            // 【删掉了 IsBlackwellGpu() ||】此前 50 系无条件返回 true → ETA/提示永远按 ONNX 慢路估算,
            // 即使实测证明 ncnn 可用。现在只看实测结论(ShouldUseOnnx* 内部:有结论用结论;
            // 没结论时只在"无独显 / Vulkan 不可用"这两种确实只能 CPU 的情况下才为真)。
            // 【2026-09-24 Real-CUGAN】它**没有 ONNX 版本** ⇒ 永远不走 ONNX,按 ncnn 估。
            // 【2026-09-27 改正括号里那句】旧写"探测不过时是 ncnn-CPU" —— 它的 CPU 档在重编版上实测会退出,
            // 探测不过的真实结果是**开跑前明确拒绝**(RealCuganNeedsGpuException),不存在 ncnn-CPU 这一档。
            if (SelectedEngineIsRealCugan) return false;
            if (!SelectedEngineIsReal)
                return EngineService.ShouldUseOnnxWaifu2x();
            return EngineService.ShouldUseOnnxEsrgan();
        }
        catch { return false; }
    }

    /// <summary>当前 UI 参数对应的经验库指纹(与 RunBtn_Click 里 ETA 校准的构造口径一致:分辨率固定按 1080p 归一,
    /// 面积倍率由 EstimateCpuUpscaleSeconds 单独乘)。只用于查"同配置上次实测秒/帧"——
    /// 查不到就用保守常数,估不准的风险有界。</summary>
    private string PerfFingerprintForCpuEstimate(out string engine)
    {
        engine = SelectedEngineName;   // 【2026-09-25 修】原来是二值 `SelectedEngineIsReal ? "realesrgan" : "waifu2x"`,
                                       // 于是选中 Real-CUGAN 时经验库指纹键被记成 "waifu2x" ⇒ 预计用时错配(只影响文字,不影响实跑引擎)。
        double scale = VideoScaleRadios.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 1 };
        if (VideoScaleRadios.SelectedIndex == 0) scale = 2;   // 1x 缩回:内部按 2x 超分再缩回(自定义分辨率已于 2026-09-25 移除)
        int interpScale = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
        bool dedupOn = DedupCheck.IsChecked == true;
        int vdenoise = DenoiseToggle.IsChecked == true ? AlhPro.Core.DenoiseStrengthOrder.ToPipeline(DenoiseStrengthIndex()) : 0;
        bool postFx = (int)SharpenSlider.Value + (int)ClaritySlider.Value + (int)UsmSlider.Value
            + (int)DetailSlider.Value + (int)PostEdgeSlider.Value > 0;
        return PerfMemory.Fingerprint(engine, scale, 1920, 1080, interpScale, dedupOn, vdenoise, postFx);
    }

    /// <summary>取"CPU 预估用的秒/帧(1080p 基准)":经验库同配置实测 与 保守常数 取【较大者】(只增不减)。
    /// 返回 (秒每帧基准, 来源说明)。</summary>
    private (double perFrameBase, string source, bool fromHistory) CpuPerFrameBase()
    {
        double floor = ALHPro.EsrganOnnxService.CpuSecondsPerFrame1080p;
        try
        {
            var hist = PerfMemory.PerFrameFor(PerfFingerprintForCpuEstimate(out _));
            if (hist.HasValue && hist.Value > floor)
                return (hist.Value, $"同配置上次实测 {hist.Value:0.##} 秒/帧(1080p 基准)", true);
        }
        catch { }
        return (floor, $"保守常数 {floor:0.#} 秒/帧(1080p 基准;出处见 EsrganOnnxService.CpuSecondsPerFrame1080p)", false);
    }

    /// <summary>刷新"超分将落到 CPU"的醒目红字(第 3 项①②:内联为主,不弹窗)。
    /// 在开始处理【之前】就能看到(与输出规格同一处提示位),所以用户不必等跑起来才知道。
    /// 数字来源:秒/帧 = max(经验库同配置实测, 保守常数) × 面积;帧数 = 时长 × 源帧率。
    /// 算不出数字(时长/尺寸探测失败)时只报"会落到 CPU",绝不编一个数出来。</summary>
    private void SetCpuFallbackHint(string? text)
    {
        try
        {
            if (CpuFallbackHint == null) return;
            if (string.IsNullOrEmpty(text))
            {
                CpuFallbackHint.Text = "";
                CpuFallbackHint.Visibility = Visibility.Collapsed;
                return;
            }
            CpuFallbackHint.Text = text;
            CpuFallbackHint.Visibility = Visibility.Visible;
        }
        catch { }
    }

    /// <summary>CPU 回落【内联红字】提示的通用尾巴(2026-10-02 事故:诊断结论多指向"独显被供电/系统关掉",
    /// 而不是"驱动太旧"——所以这里不再写"建议更新显卡驱动",改为可立即执行的排查入口)。</summary>
    private const string CpuFallbackHintTail = "。多半是独显被系统/供电关掉了(未插电、Eco/仅核显模式、在设备管理器里被禁用):"
        + "先插上电源点「重新检测」,再按日志/弹窗里的处置逐条排查";

    /// <summary>按当前选中视频刷新 CPU 回落提示(供 RefreshVideoOutSpec 在算完尺寸/帧率后调用)。</summary>
    private void RefreshCpuFallbackHint(double dur, double srcFps, int sw, int sh)
    {
        try
        {
            if (!UpscaleWillFallbackToCpu())
            {
                SetCpuFallbackHint(BackupRouteHint(dur, srcFps, sw, sh));   // null = 本次不是备用路线
                return;
            }
            var est = EstimateCpuUpscale(dur, srcFps, sw, sh);
            if (est == null)
            {
                SetCpuFallbackHint("⚠ 本机 DirectML 不可用,超分将使用 CPU(速度极慢;当前视频时长/尺寸未探明,无法给出秒/帧与总时长预估)" + CpuFallbackHintTail);
                return;
            }
            var (perFrame, stageMin, frames) = est.Value;
            SetCpuFallbackHint($"⚠ 本机 DirectML 不可用,超分将使用 CPU:约 {perFrame:0.#} 秒/帧 × {frames} 帧 → 该阶段预计约 {FormatMinutes(stageMin)}" + CpuFallbackHintTail);
        }
        catch { }
    }

    /// <summary>【2026-11-14】"这次走的是备用路线"的说明。用户 2026-11-14 定调:ONNX 只当保命兜底,
    /// 但必须当场说清慢多少、大概多久、怎么回到正常路线 —— 而不是让用户跑到一半以为软件坏了。
    /// 返回 null = 本次不是备用路线(Vulkan 正常走 ncnn 时这条永远不显示)。
    /// 只有"Vulkan 用不了 + DirectML 还能跑"才算备用路线;DirectML 也不可用时由 CPU 提示接管,不重复说。
    /// 慢多少用同卡实测比值(20~60 倍:RTX 5060 Ti 8GB ONNX 约 6 秒/帧 vs ncnn 0.1~0.3 秒/帧),
    /// 具体耗时只在"同配置有实测秒/帧"时给区间,拿不到就不编数字。</summary>
    private string? BackupRouteHint(double dur, double srcFps, int sw, int sh)
    {
        try
        {
            if (UpscaleToggle.IsChecked != true) return null;
            if (!ALHPro.VulkanCheck.NoGpuDetected) return null;        // Vulkan 正常 → 不是备用路线
            if (!ALHPro.VulkanCheck.DmlAvailable()) return null;       // DML 也不行 → 归 CPU 提示管
            if (!OnnxUpscaleRouteLikely()) return null;

            const string tail = "。多半是显卡驱动刚升级、还没重启电脑:先重启,再到「设置 → 重新检测」,回到正常路线后重新开始即可。";
            if (dur <= 0 || srcFps <= 0 || sw <= 0 || sh <= 0)
                return "⚠ 本次超分走备用路线(ONNX / DirectML):Vulkan 加速现在用不了,同一张卡上实测比正常路线慢 20~60 倍"
                    + "(时长/尺寸还没探明,给不出总耗时)" + tail;

            long frames = (long)Math.Ceiling(dur * srcFps);
            if (frames <= 0) return "⚠ 本次超分走备用路线(ONNX / DirectML):Vulkan 加速现在用不了,实测比正常路线慢 20~60 倍" + tail;

            double normPer = 0;
            try
            {
                var hist = PerfMemory.PerFrameFor(PerfFingerprintForCpuEstimate(out _));
                if (hist.HasValue && hist.Value > 0) normPer = hist.Value;   // 同配置上次实测(1080p 基准)
            }
            catch { }

            if (normPer <= 0)
                return "⚠ 本次超分走备用路线(ONNX / DirectML):Vulkan 加速现在用不了,同一张卡上实测比正常路线慢 20~60 倍"
                    + $"({frames} 帧;本配置没有实测秒/帧,给不出总耗时)" + tail;

            // 面积归一:实测秒/帧是 1080p 基准,按本次源尺寸面积缩放(与 CPU 预估同一套口径)。
            double area = Math.Max(1.0, (double)sw * sh / (1920.0 * 1080.0));
            double loMin = frames * normPer * area * 20 / 60.0;
            double hiMin = frames * normPer * area * 60 / 60.0;
            return $"⚠ 本次超分走备用路线(ONNX / DirectML):Vulkan 加速现在用不了,实测比正常路线慢 20~60 倍 →"
                + $" 该阶段大约 {FormatMinutes(loMin)} ~ {FormatMinutes(hiMin)}(按 {frames} 帧、同配置上次实测 {normPer:0.##} 秒/帧推算)" + tail;
        }
        catch { return null; }
    }

    /// <summary>预估"超分落到 CPU 后该阶段多久"。(perFrame 秒/帧, stageMin 分钟, frames 帧)。
    /// 返回 null = 信息不足(时长/尺寸/帧率未知),此时【不显示数字】(绝不编)。</summary>
    private (double perFrame, double stageMin, long frames)? EstimateCpuUpscale(double dur, double fps, int w, int h)
    {
        try
        {
            if (dur <= 0 || fps <= 0 || w <= 0 || h <= 0) return null;
            long frames = (long)Math.Ceiling(dur * fps);
            if (frames <= 0) return null;
            var (perFrameBase, _, _) = CpuPerFrameBase();
            double sec = ALHPro.EsrganOnnxService.EstimateCpuUpscaleSeconds(frames, w, h, perFrameBase, out var perFrame);
            return (perFrame, sec / 60.0, frames);
        }
        catch { return null; }
    }
}