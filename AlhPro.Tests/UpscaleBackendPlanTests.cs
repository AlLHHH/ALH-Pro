using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-25 B3】超分"走 ncnn 还是走 ONNX"的判据抽成纯函数后,把**真值表逐格钉住**。
///
/// 这段逻辑原来只存在于 `VideoService` 批循环的 if/else 里(UI 层,测试项目引用不到)⇒ 每次动超分路径都在赌。
/// 抽出来后:① 真值表可断言;② 批循环只剩一次调用(源码断言在下方);③ 真值表必须与**改动前**的 if/else
/// 逐字等价(原代码见 t27 output 的对照表;realcugan 没有 ONNX 通道 ⇒ 恒 false)。</summary>
public class UpscaleBackendPlanTests
{
    /// <summary>无 GPU / 手动选 CPU(`gpuId &lt; 0`):realesrgan 与 waifu2x 一律走 ONNX(它们的 ncnn CPU 模式
    /// 在部分机器崩,实测 exit -1 / -1073741819);realcugan 恒 false(没有 ONNX 替代)。</summary>
    [Theory]
    [InlineData("realesrgan", true)]
    [InlineData("waifu2x", true)]
    [InlineData("realcugan", false)]
    [InlineData("anime4k", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Without_a_gpu_only_esrgan_and_waifu2x_go_onnx(string? engine, bool expect)
    {
        // 注意:CPU 分支**忽略**所有"偏好 ONNX"的开关 —— 逐字等价于改动前的第一个 if(upGpu < 0)
        foreach (bool prefE in new[] { false, true })
            foreach (bool prefW in new[] { false, true })
                foreach (bool unreliable in new[] { false, true })
                    foreach (bool fast in new[] { false, true })
                        Assert.Equal(expect,
                            UpscaleBackendPlan.UseOnnx(engine, -1, prefE, prefW, unreliable, fast));
    }

    /// <summary>有 GPU(`gpuId ≥ 0`):realesrgan 看 `ShouldUseOnnxEsrgan()` / ncnn 不可靠 / 快模式;
    /// waifu2x 看 `ShouldUseOnnxWaifu2x()‖waifuOnnx` / ncnn 不可靠 / 快模式;**realcugan 恒 false**
    /// (即使 ncnn 被判不可靠、即使开了快模式 —— 它没有 ONNX 通道,改动前后的行为一致)。</summary>
    [Fact]
    public void With_a_gpu_the_three_engines_follow_the_original_truth_table()
    {
        // realesrgan:三个开关任一为真 → ONNX
        Assert.False(UpscaleBackendPlan.UseOnnx("realesrgan", 0, false, true, false, false));
        Assert.True(UpscaleBackendPlan.UseOnnx("realesrgan", 0, true, false, false, false));
        Assert.True(UpscaleBackendPlan.UseOnnx("realesrgan", 0, false, false, true, false));
        Assert.True(UpscaleBackendPlan.UseOnnx("realesrgan", 0, false, false, false, true));

        // waifu2x:同上,但"是否优先"那一项是 ShouldUseOnnxWaifu2x()‖waifuOnnx(由调用方合成)
        Assert.False(UpscaleBackendPlan.UseOnnx("waifu2x", 0, true, false, false, false));
        Assert.True(UpscaleBackendPlan.UseOnnx("waifu2x", 0, false, true, false, false));
        Assert.True(UpscaleBackendPlan.UseOnnx("waifu2x", 0, false, false, true, false));
        Assert.True(UpscaleBackendPlan.UseOnnx("waifu2x", 0, false, false, false, true));

        // realcugan / 未知引擎:恒 false
        foreach (bool prefE in new[] { false, true })
            foreach (bool prefW in new[] { false, true })
                foreach (bool unreliable in new[] { false, true })
                    foreach (bool fast in new[] { false, true })
                    {
                        Assert.False(UpscaleBackendPlan.UseOnnx("realcugan", 0, prefE, prefW, unreliable, fast));
                        Assert.False(UpscaleBackendPlan.UseOnnx("其它引擎", 3, prefE, prefW, unreliable, fast));
                    }
    }

    /// <summary>穷举:函数必须**纯**(同参数同结果,不抛异常),且返回值只取决于六个参数。</summary>
    [Fact]
    public void UseOnnx_is_pure_and_total()
    {
        int seen = 0;
        foreach (var engine in new[] { "realesrgan", "waifu2x", "realcugan", null })
            foreach (int gpu in new[] { -2, -1, 0, 1 })
                foreach (bool a in new[] { false, true })
                    foreach (bool b in new[] { false, true })
                        foreach (bool c in new[] { false, true })
                            foreach (bool d in new[] { false, true })
                            {
                                bool x = UpscaleBackendPlan.UseOnnx(engine, gpu, a, b, c, d);
                                bool y = UpscaleBackendPlan.UseOnnx(engine, gpu, a, b, c, d);
                                Assert.Equal(x, y);
                                seen++;
                            }
        Assert.Equal(4 * 4 * 16, seen);
    }

    /// <summary>**源码断言**:批循环必须改用这个纯函数(不再自己写 if/else),而且原来那串判据不许残留在
    /// `VideoService` 里(残留 = 出现第二个判据,以后两处会各说各话)。</summary>
    [Fact]
    public void The_batch_loop_uses_the_pure_function_instead_of_its_own_if_else()
    {
        string svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("AlhPro.Core.UpscaleBackendPlan.UseOnnx(", svc);
        // 改动前的三种"自己判"写法都必须消失(拿旧代码模式扫,不拿名字扫:注释里会写"原来是什么")
        Assert.DoesNotContain("(EngineService.ShouldUseOnnxEsrgan() || ncnnUnreliable || fastMode)", svc);
        Assert.DoesNotContain("(EngineService.ShouldUseOnnxWaifu2x() || waifuOnnx || ncnnUnreliable || fastMode)", svc);
        Assert.DoesNotContain("else if (engine == \"realesrgan\" && (", svc);
        // 取路径这一步仍在(realcugan 没有 ONNX 路径;waifu2x 与 realesrgan 各一支)
        Assert.Contains("EsrganOnnxService.ResolveEsrganOnnxPath(model)", svc);
        Assert.Contains("EsrganOnnxService.FindWaifu2xModel(model)", svc);
        // 【2026-09-25 修订 · F1】ONNX 设备号也必须走同一个纯函数(标定与批次循环不许各写一份)
        Assert.Contains("AlhPro.Core.UpscaleBackendPlan.OnnxDevice(upGpu, upOnnxDml)", svc);
        Assert.DoesNotContain("upGpu < 0 ? (upOnnxDml ? -2 : -1) : -2", svc);
        // 判定点必须用同一个纯函数算"本次后端",并按它查/存单价
        Assert.Contains("AlhPro.Core.UpscaleBackendPlan.DescribeBackend(", svc);
        Assert.Contains("string calibBackend = AlhPro.Core.UpscaleBackendPlan.DescribeBackend(", svc);
    }

    // ═══════════ 【2026-09-25 修订 · F1】后端身份(ncnn / ONNX-DirectML / ONNX-CPU)═══════════

    /// <summary>后端名归一 + 中文标签:认得出的归一,认不出的当"未确认"(空白)。</summary>
    [Theory]
    [InlineData("ncnn-vulkan", "ncnn-vulkan")]
    [InlineData("NCNN", "ncnn-vulkan")]
    [InlineData(" ncnn ", "ncnn-vulkan")]
    [InlineData("onnx-dml", "onnx-dml")]
    [InlineData("onnx", "onnx-dml")]
    [InlineData("ONNX-CPU", "onnx-cpu")]
    [InlineData("vulkan-2027", "")]        // 认不出 ⇒ 未确认(不许落盘)
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Backend_names_are_normalized(string? raw, string expect)
        => Assert.Equal(expect, UpscaleBackendPlan.NormalizeBackend(raw));

    /// <summary>设备公式与批次循环改动前逐字等价:`gpuId &lt; 0` 时看 onnxDml(-2 DirectML / -1 CPU),
    /// 否则一律 -2(自适应)。</summary>
    [Theory]
    [InlineData(-1, false, -1)]
    [InlineData(-1, true, -2)]
    [InlineData(-2, false, -1)]
    [InlineData(0, false, -2)]
    [InlineData(0, true, -2)]
    [InlineData(3, true, -2)]
    public void OnnxDevice_matches_the_original_formula(int gpuId, bool onnxDml, int expect)
        => Assert.Equal(expect, UpscaleBackendPlan.OnnxDevice(gpuId, onnxDml));

    /// <summary>`DescribeBackend` = "走不走 ONNX" ⊗ "ONNX 用哪个设备",与真值表同源:
    /// ncnn 可用 ⇒ ncnn-vulkan;-1 ⇒ onnx-cpu;-2 ⇒ onnx-dml;realcugan 无 GPU ⇒ 未确认(绝不能落盘)。</summary>
    [Fact]
    public void DescribeBackend_derives_the_persisted_key_from_the_same_truth_table()
    {
        // 有 GPU、三个开关都关 ⇒ ncnn
        Assert.Equal(UpscaleBackendPlan.NcnnVulkan,
            UpscaleBackendPlan.DescribeBackend("realesrgan", 0, false, false, false, false, onnxDml: false));
        // 有 GPU + 快模式 ⇒ ONNX,设备 -2(DirectML)
        Assert.Equal(UpscaleBackendPlan.OnnxDml,
            UpscaleBackendPlan.DescribeBackend("realesrgan", 0, false, false, false, true, onnxDml: false));
        // 探测失败改口:upGpu=-1 + upOnnxDml=true ⇒ ONNX DirectML(不是 CPU)
        Assert.Equal(UpscaleBackendPlan.OnnxDml,
            UpscaleBackendPlan.DescribeBackend("realesrgan", -1, false, false, false, false, onnxDml: true));
        // 用户主动选 CPU(-g -1)且无 DirectML ⇒ ONNX CPU
        Assert.Equal(UpscaleBackendPlan.OnnxCpu,
            UpscaleBackendPlan.DescribeBackend("realesrgan", -1, false, false, false, false, onnxDml: false));
        // waifu2x 探测失败走 ONNX 整段(waifuOnnx 由调用方合成进 onnxPreferredWaifu2x)⇒ ONNX
        Assert.Equal(UpscaleBackendPlan.OnnxDml,
            UpscaleBackendPlan.DescribeBackend("waifu2x", 0, false, true, false, false, onnxDml: true));
        // realcugan 没有 ONNX 通道:有 GPU ⇒ ncnn;连 GPU 都没有 ⇒ **未确认**(上层会先 throw,这里也不许落盘)
        Assert.Equal(UpscaleBackendPlan.NcnnVulkan,
            UpscaleBackendPlan.DescribeBackend("realcugan", 0, false, false, true, true, onnxDml: true));
        Assert.Equal(UpscaleBackendPlan.Unknown,
            UpscaleBackendPlan.DescribeBackend("realcugan", -1, false, false, false, false, onnxDml: true));
        // 未确认的后端一律不许当键用
        Assert.Null(LocalPriceBook.PerFrame1080p(
            new[] { LocalPriceFixture.At1080p("models-se:0", 2, 1.65) }, "models-se:0", 2, out _,
            LocalPriceFixture.MachineKey, UpscaleBackendPlan.Unknown));
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
