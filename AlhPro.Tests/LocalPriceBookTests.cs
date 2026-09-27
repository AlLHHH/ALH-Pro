using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-25 A+B】本机实测超分单帧耗时的**记录/折算/拒收**单测(契约 A1)。
///
/// 缺陷背景:顺序判定只读 `PipelineOrderPlan.UpscaleRates`(开发机一台机器的实测)⇒
/// 「别人的机器用我的机器的秒/帧决定先超分还是先补帧」(用户 2026-09-25 原话)。
/// 本文件钉的是新口径的**数据面**:`SecondsPerFrame1080p` 的面积折算、`TryBuild` 的六条拒收、
/// 机器指纹不匹配不采信、JSON 容错。</summary>
public class LocalPriceBookTests
{
    private const long Px1080 = 1920L * 1080;

    // ───────────────────────── SecondsPerFrame1080p:面积折算 ─────────────────────────

    /// <summary>采样在 1080p 上 ⇒ 折算值就是原值;采样在 4K 上 ⇒ 除以 4(超分成本 ∝ 源面积)。</summary>
    [Fact]
    public void SecondsPerFrame1080p_scales_by_sampled_area()
    {
        var p1080 = LocalPriceFixture.AtPixels("realesr-animevideov3", 2, 0.26, Px1080);
        Assert.Equal(0.26, p1080.SecondsPerFrame1080p, 9);

        var p4k = LocalPriceFixture.AtPixels("realesr-animevideov3", 2, 1.04, Px1080 * 4);   // 2160p 源
        Assert.Equal(0.26, p4k.SecondsPerFrame1080p, 9);

        // 面积非法(0)时不许除零、也不许算出 Infinity:原样返回 + 由 TryBuild 负责拒收
        var bad = LocalPriceFixture.AtPixels("realesr-animevideov3", 2, 0.26, 0);
        Assert.Equal(0.26, bad.SecondsPerFrame1080p, 9);
    }

    /// <summary>出处一行必须能被审计:两点法的两次耗时 + N + 采样分辨率 + 折算到 1080p 的单帧耗时(契约 A4)。</summary>
    [Fact]
    public void Provenance_cites_two_timings_frames_resolution_and_the_1080p_conversion()
    {
        var p = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, sampleFrames: 6);
        string s = p.Provenance;
        Assert.Contains("本机实测", s);
        Assert.Contains("两点法", s);
        Assert.Contains("1 帧", s);
        Assert.Contains("6 帧", s);
        Assert.Contains("1920×1080", s);
        Assert.Contains("1.65", s);
        Assert.Contains("折算 1080p", s);
        // 两次耗时与算出的单帧耗时必须自洽:0.9 + 6×1.65 = 10.8
        Assert.Contains("10.8", s);
    }

    // ───────────────────────── TryBuild:成功与六条拒收 ─────────────────────────

    /// <summary>两点法自洽的一组数 ⇒ 通过,且 `SecondsPerFrame` 就是扣掉地板后的单帧耗时;
    /// `FloorSeconds`/`SampleSeconds` 是**两次的原始耗时**(可审计,不是算出来的地板)。</summary>
    [Fact]
    public void TryBuild_accepts_a_self_consistent_two_point_sample()
    {
        const double floor = 0.9, per = 1.65;
        int n = 6;
        double t1 = floor + per, tN = floor + n * per;
        bool ok = LocalPriceBook.TryBuild("models-se:0", 2, n, t1, tN, Px1080,
            "机器A", UpscaleBackendPlan.NcnnVulkan, "2026-09-25 12:00:00", "单测", out var price, out string reject);
        Assert.True(ok, reject);
        Assert.Equal("", reject);
        Assert.Equal(per, price.SecondsPerFrame, 9);
        Assert.Equal("realcugan-se", price.ModelKey);      // 引擎侧名 `models-se:-1` 归一成表键
        Assert.Equal(2, price.EngineScale);
        Assert.Equal(n, price.SampleFrames);
        Assert.Equal(t1, price.FloorSeconds, 9);           // 完整可审计:两次原始耗时都在
        Assert.Equal(tN, price.SampleSeconds, 9);
        Assert.Equal(floor, CalibrationSample.TwoPointFloor(n, t1, tN), 9);   // 算出的地板 = 0.9
        Assert.Equal(per, price.SecondsPerFrame1080p, 9);
        Assert.Equal(UpscaleBackendPlan.NcnnVulkan, price.Backend);   // 【F1】后端必须记进记录
    }

    /// <summary>【契约点名的拒收】两点法退化 `tN &lt;= t1`:等于没测出正斜率 ⇒ 必须拒收,
    /// **不许**回退成 `sampleSeconds / N`(那正是把"每进程地板"当成单帧成本的老错)。</summary>
    [Theory]
    [InlineData(4.0, 4.0)]      // 完全相等
    [InlineData(4.0, 3.7)]      // N 帧那次反而更快(噪声)
    [InlineData(4.0, 1.0)]      // 明显慢于 0(但仍是正数 ⇒ 走"退化"这条,而不是"耗时非法")
    public void TryBuild_rejects_degenerate_samples_and_never_falls_back_to_total_over_frames(double floor, double sample)
    {
        bool ok = LocalPriceBook.TryBuild("realesr-animevideov3", 2, 6, floor, sample, Px1080,
            "机器A", UpscaleBackendPlan.NcnnVulkan, "t", "", out var price, out string reject);
        Assert.False(ok);
        Assert.Equal(default(LocalPrice), price);           // 没有产出任何记录
        Assert.Contains("退化", reject);
        Assert.Contains("不回退", reject);                   // 明确写了"不许回退成 总量÷帧数"
        Assert.Contains("拒收", reject);
        // 那句"总量÷帧数"的数字(4.0/6≈0.67)绝不能作为单帧耗时出现 —— 记录根本没生成
        Assert.NotEqual(0.667, price.SecondsPerFrame, 3);
    }

    /// <summary>其余拒收条件逐条走一遍(N&lt;4 / 两次耗时非法 / 面积非法 / 机器指纹空白 / 单帧耗时越界 / 认不出模型)。</summary>
    [Fact]
    public void TryBuild_rejects_every_invalid_input_with_a_readable_reason()
    {
        static bool Try(string model, int scale, int frames, double floor, double sample, long px, string key, out string reject,
            string backend = UpscaleBackendPlan.NcnnVulkan)
            => LocalPriceBook.TryBuild(model, scale, frames, floor, sample, px, key, backend, "t", "", out _, out reject);

        // ① 采样帧数 < MinSampleFrames
        Assert.False(Try("realesr-animevideov3", 2, LocalPriceBook.MinSampleFrames - 1, 0.9, 0.9 + 3 * 0.26, Px1080, "K", out string r1));
        Assert.Contains("采样帧数太少", r1);

        // ② 引擎倍率非法
        Assert.False(Try("realesr-animevideov3", 0, 6, 0.9, 2.5, Px1080, "K", out string r2));
        Assert.Contains("引擎倍率非法", r2);

        // ③ 1 帧那次耗时非法(0 / NaN)
        Assert.False(Try("realesr-animevideov3", 2, 6, 0, 2.5, Px1080, "K", out string r3));
        Assert.Contains("1 帧那次耗时非法", r3);
        Assert.False(Try("realesr-animevideov3", 2, 6, double.NaN, 2.5, Px1080, "K", out string r3b));
        Assert.Contains("1 帧那次耗时非法", r3b);

        // ④ N 帧那次耗时非法(负 / 非有限)
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, -1, Px1080, "K", out string r4));
        Assert.Contains("6 帧那次耗时非法", r4);
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, double.PositiveInfinity, Px1080, "K", out string r4b));
        Assert.Contains("6 帧那次耗时非法", r4b);

        // ⑤ 采样面积非法
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, 2.5, 0, "K", out string r5));
        Assert.Contains("采样源面积非法", r5);

        // ⑥ 机器指纹空白(标定不能不知道"这是哪台机器")
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, 2.5, Px1080, "   ", out string r6));
        Assert.Contains("机器指纹为空", r6);

        // ⑦ 认不出的模型名
        Assert.False(Try("some-new-model", 2, 6, 0.9, 2.5, Px1080, "K", out string r7));
        Assert.Contains("认不出这个模型名", r7);

        // ⑧ 后端未确认(空白 / 认不出)——【F1-I4】后端没定稿就不许产出可落盘的单帧耗时
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, 2.5, Px1080, "K", out string r8a, backend: "   "));
        Assert.Contains("超分后端未确认", r8a);
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, 2.5, Px1080, "K", out string r8b, backend: "vulkan-2027"));
        Assert.Contains("超分后端未确认", r8b);

        // ⑨ 单帧耗时下限:算出来 0.0004 s/帧 ≤ 0.0005 ⇒ 拒收。
        // 【取样口径】必须让**差值先过大**(≥ 0.15×t1),否则会先被 ⑪ 那条相对噪声门槛拦下 ——
        // 所以这里把 t1 也压小(0.001s):差值 3×0.0004=0.0012 ≥ 0.15×0.001=0.00015 ✔ ⇒ 才轮到"合理区间"这条。
        Assert.False(Try("realesr-animevideov3", 2, 4, 0.001, 0.001 + 3 * 0.0004, Px1080, "K", out string r9a));
        Assert.Contains("超出合理区间", r9a);

        // ⑩ 单帧耗时上限:算出来 200 s/帧(> 120);差值 5×200=1000s 远超噪声门槛 ⇒ 走"合理区间"这条
        Assert.False(Try("realesr-animevideov3", 2, 6, 0.9, 0.9 + 5 * 200, Px1080, "K", out string r9));
        Assert.Contains("超出合理区间", r9);

        // ⑪ 【F3 · 相对噪声门槛(2026-09-25 修订)】差值落在"两次进程启动抖动"量级 ⇒ 拒收,
        //    而且拒收理由必须写明差值、门槛比例与理由(契约点名的反例:t1=0.50 / tN=0.55 / N=4)
        Assert.False(Try("realesr-animevideov3", 2, 4, 0.50, 0.55, Px1080, "K", out string r11));
        Assert.Contains("两点法差值太小", r11);
        Assert.Contains("0.05", r11);                       // 差值
        Assert.Contains("15%", r11);                        // 门槛比例
        Assert.Contains("抖动", r11);                       // 理由(与进程启动抖动同量级)
        Assert.Contains("假精度", r11);
    }

    // ───────────────────────── 机器指纹:别人的机器一律不采信 ─────────────────────────

    /// <summary>同一格:指纹一致 ⇒ 可用;指纹不一致 ⇒ **不可用**并报出"另一台机器";不做机器过滤时按资料口径可用。</summary>
    [Fact]
    public void Resolve_only_trusts_a_matching_machine_fingerprint()
    {
        var mine = LocalPriceFixture.At1080p("realesr-animevideov3", 2, 0.2605, LocalPriceFixture.MachineKey);
        var theirs = LocalPriceFixture.At1080p("realesr-animevideov3", 2, 9.9, LocalPriceFixture.OtherMachineKey);

        var ok = LocalPriceBook.Resolve(new[] { theirs, mine }, "realesr-animevideov3", 2, LocalPriceFixture.MachineKey);
        Assert.True(ok.Usable);
        Assert.Equal(0.2605, ok.Price!.Value.SecondsPerFrame1080p, 9);   // 别人的 9.9 绝不许被采信

        var other = LocalPriceBook.Resolve(new[] { theirs }, "realesr-animevideov3", 2, LocalPriceFixture.MachineKey);
        Assert.False(other.Usable);
        Assert.Null(other.Price);
        Assert.Equal(LocalPriceFixture.OtherMachineKey, other.OtherMachineKey);

        // 不做机器过滤(资料/单测口径):任一条命中即可用
        var noFilter = LocalPriceBook.Resolve(new[] { theirs }, "realesr-animevideov3", 2, machineKey: null);
        Assert.True(noFilter.Usable);

        // 三参版 Find 同理(资料查询)
        Assert.NotNull(LocalPriceBook.Find(new[] { mine }, "realesr-animevideov3", 2));
        Assert.Null(LocalPriceBook.Find(new[] { mine }, "realesr-animevideov3", 4));   // 倍率不同⇒不同格
    }

    /// <summary>同一格存在多条(重标过):取 `MeasuredAtUtc` 最新那条;指纹不符时 `PerFrame1080p` 返回 null 并把
    /// "已忽略另一台机器"写进出处。</summary>
    [Fact]
    public void Resolve_prefers_the_newest_record_and_explains_an_ignored_one()
    {
        var older = LocalPriceFixture.At1080p("models-se:0", 2, 1.80, LocalPriceFixture.MachineKey) with { MeasuredAtUtc = "2026-09-24 09:00:00" };
        var newer = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, LocalPriceFixture.MachineKey) with { MeasuredAtUtc = "2026-09-25 09:00:00" };
        var theirs = LocalPriceFixture.At1080p("models-se:0", 2, 0.30, LocalPriceFixture.OtherMachineKey);

        var hit = LocalPriceBook.Resolve(new[] { older, theirs, newer }, "models-se:0", 2, LocalPriceFixture.MachineKey);
        Assert.True(hit.Usable);
        Assert.Equal(1.65, hit.Price!.Value.SecondsPerFrame1080p, 9);
        Assert.Equal(LocalPriceFixture.OtherMachineKey, hit.OtherMachineKey);

        double? p = LocalPriceBook.PerFrame1080p(new[] { theirs }, "models-se:0", 2, out string prov, LocalPriceFixture.MachineKey);
        Assert.Null(p);
        Assert.Contains("另一台机器", prov);
        Assert.Contains("已忽略", prov);

        // 完全没有这一格 ⇒ 出处说明"本机没有这一格"
        Assert.Null(LocalPriceBook.PerFrame1080p(new[] { theirs }, "models-se:0", 4, out string prov2, LocalPriceFixture.MachineKey));
        Assert.Contains("本机没有这一格", prov2);
    }

    /// <summary>`Upsert` 去重口径:同 (模型键, 倍率, 机器指纹) 覆盖;别的机器的记录原样保留(可审计)。</summary>
    [Fact]
    public void Upsert_replaces_the_same_slot_and_keeps_other_machines_records()
    {
        var old1 = LocalPriceFixture.At1080p("models-se:0", 2, 1.80, LocalPriceFixture.MachineKey);
        var theirs = LocalPriceFixture.At1080p("models-se:0", 2, 0.30, LocalPriceFixture.OtherMachineKey);
        var fresh = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, LocalPriceFixture.MachineKey);

        var book = LocalPriceBook.Upsert(new[] { old1, theirs }, fresh);
        Assert.Equal(2, book.Count);                                     // 去重:本机那条被覆盖,不是新增
        Assert.Contains(book, p => p.MachineKey == LocalPriceFixture.MachineKey && Math.Abs(p.SecondsPerFrame1080p - 1.65) < 1e-9);
        Assert.Contains(book, p => p.MachineKey == LocalPriceFixture.OtherMachineKey);   // 别人的留着

        // 空表也能写进去
        Assert.Single(LocalPriceBook.Upsert(null, fresh));
        Assert.Equal("realcugan-se@2x", LocalPriceBook.Key(fresh.ModelKey, fresh.EngineScale));
        // 【F1-I2】判定用的键**含后端**;两参重载(不含后端)只作资料显示
        Assert.Equal("realcugan-se@2x@ncnn-vulkan",
            LocalPriceBook.Key(fresh.ModelKey, fresh.EngineScale, fresh.Backend));
        Assert.NotEqual(LocalPriceBook.Key(fresh.ModelKey, fresh.EngineScale, UpscaleBackendPlan.OnnxDml),
            LocalPriceBook.Key(fresh.ModelKey, fresh.EngineScale, fresh.Backend));
        // 去重也必须按后端分开:同一模型在两条后端上的记录**互不覆盖**
        var ncnnRec = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, LocalPriceFixture.MachineKey, backend: UpscaleBackendPlan.NcnnVulkan);
        var onnxRec = LocalPriceFixture.At1080p("models-se:0", 2, 5.10, LocalPriceFixture.MachineKey, backend: UpscaleBackendPlan.OnnxDml);
        var both = LocalPriceBook.Upsert(LocalPriceBook.Upsert(null, ncnnRec), onnxRec);
        Assert.Equal(2, both.Count);
    }

    // ───────────────────────── JSON 落盘格式与容错 ─────────────────────────

    /// <summary>写出去再读回来必须逐字段相等(schema 1);这条是"标定能跨任务复用"的基础。</summary>
    [Fact]
    public void Json_round_trips_every_field()
    {
        var a = LocalPriceFixture.At1080p("models-se:0", 2, 1.65);
        var b = LocalPriceFixture.At1080p("realesr-animevideov3", 2, 0.2605, LocalPriceFixture.OtherMachineKey);
        string json = LocalPriceBook.ToJson(new[] { a, b });
        Assert.Contains("\"schema\": 1", json);
        Assert.Contains("\"prices\"", json);

        var back = LocalPriceBook.ParseJson(json);
        Assert.Equal(2, back.Count);
        Assert.Equal(a, back[0]);      // record struct 值相等:字段全对(含两次耗时/时间/指纹/备注)
        Assert.Equal(b, back[1]);
    }

    /// <summary>**容错**:文件缺失 / 截断 / 结构不对 / schema 不认识 / 单条字段非法 ⇒ 空表或只丢坏的那条,
    /// **一律不抛**(标定文件坏掉不许影响任务)。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]                                   // 截断
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]                                  // 顶层是数组(旧格式/手工改坏)
    [InlineData("{\"schema\":1}")]                      // 没有 prices
    [InlineData("{\"schema\":99,\"prices\":[{\"modelKey\":\"cunet\",\"engineScale\":2,\"secondsPerFrame\":0.37,\"samplePixels\":2073600,\"sampleFrames\":6,\"machineKey\":\"K\"}]}")]
    public void ParseJson_never_throws_and_falls_back_to_an_empty_book(string? json)
        => Assert.Empty(LocalPriceBook.ParseJson(json));

    /// <summary>逐条校验:坏的只丢它自己,好的要留下;`schema` 对但字段缺(如无指纹/面积为 0)也算坏的那条。</summary>
    [Fact]
    public void ParseJson_drops_only_the_broken_entries()
    {
        string json = """
        {"schema":1,"prices":[
          {"modelKey":"realcugan-se","engineScale":2,"secondsPerFrame":1.65,"samplePixels":2073600,"sampleFrames":6,
           "floorSeconds":0.9,"sampleSeconds":10.8,"measuredAtUtc":"2026-09-25 12:00:00","machineKey":"K","backend":"ncnn-vulkan","note":"ok"},
          {"modelKey":"cunet","engineScale":2,"secondsPerFrame":0.37,"samplePixels":2073600,"sampleFrames":6,"machineKey":""},
          {"modelKey":"cunet","engineScale":2,"secondsPerFrame":0,"samplePixels":2073600,"sampleFrames":6,"machineKey":"K"},
          {"modelKey":"cunet","engineScale":2,"secondsPerFrame":0.37,"samplePixels":0,"sampleFrames":6,"machineKey":"K"},
          {"modelKey":"","engineScale":2,"secondsPerFrame":0.37,"samplePixels":2073600,"sampleFrames":6,"machineKey":"K"},
          {"modelKey":"cunet","engineScale":2,"secondsPerFrame":0.37,"samplePixels":2073600,"sampleFrames":6,"machineKey":"K","backend":"ncnn-vulkan"},
          {"modelKey":"cunet","engineScale":2,"secondsPerFrame":0.37,"samplePixels":2073600,"sampleFrames":6,"machineKey":"K","backend":"vulkan-2027"}
        ]}
        """;
        var book = LocalPriceBook.ParseJson(json);
        // 留下 2 条:① 本机 + 后端认得出;⑥ 同上(第二个模型)。丢掉的 5 条分别是:
        // 无指纹 / 单帧耗时 0 / 面积为 0 / 模型名为空 / **backend 认不出(F1-I4:后端未确认的记录不许存在)**。
        Assert.Equal(2, book.Count);
        Assert.Equal("realcugan-se", book[0].ModelKey);
        Assert.Equal(1.65, book[0].SecondsPerFrame, 9);
        Assert.Equal("ncnn-vulkan", book[0].Backend);
        Assert.Equal("ok", book[0].Note);
        Assert.Equal("cunet", book[1].ModelKey);
        Assert.Equal(UpscaleBackendPlan.NcnnVulkan, book[1].Backend);
    }

    /// <summary>机器指纹摘要只用于日志显示(不泄漏整串),空指纹有明确写法。</summary>
    [Fact]
    public void Digest_is_short_and_explicit_about_a_blank_key()
    {
        Assert.Equal("(空指纹)", LocalPriceBook.Digest(""));
        Assert.Equal("(空指纹)", LocalPriceBook.Digest(null));
        Assert.Equal("短指纹", LocalPriceBook.Digest("短指纹"));
        string longKey = new string('A', 60);
        string d = LocalPriceBook.Digest(longKey);
        Assert.True(d.Length < longKey.Length);
        Assert.Contains("…", d);
    }

    /// <summary>常量口径(与契约一致):MinSampleFrames = 4、单帧耗时区间 [0.0005, 120]、schema = 1。</summary>
    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(4, LocalPriceBook.MinSampleFrames);
        Assert.Equal(0.0005, LocalPriceBook.MinSecondsPerFrame, 9);
        Assert.Equal(120.0, LocalPriceBook.MaxSecondsPerFrame, 9);
        Assert.Equal(1, LocalPriceBook.SchemaVersion);
        Assert.Equal("realcugan-se@2x", LocalPriceBook.Key("realcugan-se", 2));
    }

    /// <summary>`Key` 与 `Resolve` 的键必须同源:归一后的模型键 + 引擎倍率(+ 后端)。</summary>
    [Fact]
    public void Key_matches_what_resolve_looks_up()
    {
        var p = LocalPriceFixture.At1080p("models-se:-1", 3, 1.7);
        Assert.Equal(LocalPriceBook.Key("realcugan-se", 3), LocalPriceBook.Key(p.ModelKey, p.EngineScale));
        Assert.True(LocalPriceBook.Resolve(new[] { p }, "models-se:3", 3).Usable);   // 另一个降噪档 ⇒ 同一格
    }

    // ═══════════════ 【2026-09-25 修订 · F1-I2】后端不得串用(后端进键) ═══════════════

    /// <summary>**I2 的核心断言**:在 ncnn 上测的单帧耗时,查"ONNX 后端"这一格时必须**查不到**
    /// (否则就会拿偏小的 ncnn 单帧耗时去判定 ONNX 运行 —— 2026-09-25 那次 39 分钟误判的同一类)。
    /// 【红检】把 Resolve 里的后端过滤那行删掉(或让 Key 不含后端),本用例立刻变红。</summary>
    [Fact]
    public void A_price_measured_on_one_backend_is_never_usable_for_another()
    {
        var ncnn = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, backend: UpscaleBackendPlan.NcnnVulkan);
        var book = new[] { ncnn };

        // 同后端 ⇒ 命中且可用
        Assert.True(LocalPriceBook.Resolve(book, "models-se:0", 2, null, UpscaleBackendPlan.NcnnVulkan).Usable);
        // 另外两条后端 ⇒ 一格都没有(不是"退化成不看后端")
        Assert.False(LocalPriceBook.Resolve(book, "models-se:0", 2, null, UpscaleBackendPlan.OnnxDml).Usable);
        Assert.False(LocalPriceBook.Resolve(book, "models-se:0", 2, null, UpscaleBackendPlan.OnnxCpu).Usable);
        Assert.Null(LocalPriceBook.PerFrame1080p(book, "models-se:0", 2, out _, null, UpscaleBackendPlan.OnnxDml));
        // 不传后端(null)= 资料式提问,按"本机有没有这一格"回答 ⇒ 命中
        Assert.True(LocalPriceBook.Resolve(book, "models-se:0", 2, null, null).Usable);

        // 两条后端各有一条记录时:各查各的,数字不许串
        var dml = LocalPriceFixture.At1080p("models-se:0", 2, 5.10, backend: UpscaleBackendPlan.OnnxDml);
        var both = new[] { ncnn, dml };
        Assert.Equal(1.65, LocalPriceBook.PerFrame1080p(both, "models-se:0", 2, out _, null, UpscaleBackendPlan.NcnnVulkan)!.Value, 9);
        Assert.Equal(5.10, LocalPriceBook.PerFrame1080p(both, "models-se:0", 2, out _, null, UpscaleBackendPlan.OnnxDml)!.Value, 9);

        // 后端名归一:大小写/简写都认;认不出的当"未确认"(查任何格都不命中)
        Assert.Equal(UpscaleBackendPlan.NcnnVulkan, UpscaleBackendPlan.NormalizeBackend(" NCNN "));
        Assert.Equal(UpscaleBackendPlan.OnnxDml, UpscaleBackendPlan.NormalizeBackend("ONNX"));
        Assert.Equal(UpscaleBackendPlan.Unknown, UpscaleBackendPlan.NormalizeBackend("vulkan-2027"));
        Assert.False(LocalPriceBook.Resolve(book, "models-se:0", 2, null, "vulkan-2027").Usable);
    }

    /// <summary>出处一行必须写出后端 —— 否则两次不同后端的标定在日志里长得一样,事后没法核。</summary>
    [Fact]
    public void Provenance_names_the_backend()
    {
        var a = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, backend: UpscaleBackendPlan.NcnnVulkan);
        var b = LocalPriceFixture.At1080p("models-se:0", 2, 5.10, backend: UpscaleBackendPlan.OnnxDml);
        Assert.Contains("后端 ncnn-Vulkan", a.Provenance);
        Assert.Contains("后端 ONNX 稳定引擎(DirectML GPU,device -2)", b.Provenance);
    }

    /// <summary>【I2 · 判定层】已标定,但标定是在**另一条后端**上做的 ⇒ 这次判定**不许采用**它,
    /// 而且理由必须写明"本机未标定",不许静默换个数/悄悄回退到内置表。
    /// 【红检】把 `Decide` 里传给 `Resolve` 的 `backend` 去掉(或让 Resolve 把"认不出"当"不过滤"),
    /// 第二条断言的 `Measured`/`UpscaleFirst` 立刻翻成 true ⇒ 变红。</summary>
    [Fact]
    public void A_calibration_from_another_backend_is_not_adopted_by_the_decision()
    {
        var ncnn = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, backend: UpscaleBackendPlan.NcnnVulkan);
        var book = new[] { ncnn };

        // ① 同后端:采用本机数字 ⇒ 1.65 s/帧 ≫ 门槛 0.2625 s ⇒ 新顺序
        var same = PipelineOrderPlan.Decide("realcugan", "models-se:0", 2.0, 4, 1920, 1080, 1800,
            localPrices: book, machineKey: LocalPriceFixture.MachineKey, backend: UpscaleBackendPlan.NcnnVulkan);
        Assert.True(same.Measured);
        Assert.True(same.UpscaleFirst);

        // ② 另一条后端:那一格不存在 ⇒ 未标定 → 旧顺序 + 理由写明【本机未标定】
        var other = PipelineOrderPlan.Decide("realcugan", "models-se:0", 2.0, 4, 1920, 1080, 1800,
            localPrices: book, machineKey: LocalPriceFixture.MachineKey, backend: UpscaleBackendPlan.OnnxDml);
        Assert.False(other.Measured);
        Assert.False(other.UpscaleFirst);
        Assert.Contains("本机未标定", other.Reason);
        // 【R2 · 2026-09-25 修订】还要明写"另一后端的标定已忽略"——像机器指纹那条一样,
        // 别让用户以为"本机压根没标过"(否则他下次还在同一条路上白等)。
        Assert.Contains("另一后端", other.Reason);
        Assert.Contains("已忽略", other.Reason);
        Assert.Contains("ncnn-Vulkan", other.Reason);           // 被忽略的那条后端要说清是哪条
        Assert.Contains(UpscaleBackendPlan.Label(UpscaleBackendPlan.OnnxDml), other.Reason);   // 本次后端也在场
        // 判定结果不变:仍然不采用、仍然旧顺序
        Assert.Equal(other.UpscaleFirst, PipelineOrderPlan.Decide("realcugan", "models-se:0", 2.0, 4, 1920, 1080, 1800,
            localPrices: null, machineKey: LocalPriceFixture.MachineKey, backend: UpscaleBackendPlan.OnnxDml).UpscaleFirst);

        // ③ 认不出的后端名同样一格都不命中(不是"不过滤")
        var bogus = PipelineOrderPlan.Decide("realcugan", "models-se:0", 2.0, 4, 1920, 1080, 1800,
            localPrices: book, machineKey: LocalPriceFixture.MachineKey, backend: "vulkan-2027");
        Assert.False(bogus.Measured);
        Assert.Contains("本机未标定", bogus.Reason);
    }

    /// <summary>【R2】`Lookup.OtherBackend` 的存在与语义:同一台机器、同一模型倍率,但后端与本次不同 ⇒
    /// 报出那条后端名(供理由/界面短话使用);后端一致或压根没有别后端时为 null。
    /// 出处文本(`PerFrame1080p`)也要用上它。</summary>
    [Fact]
    public void Lookup_reports_a_same_machine_calibration_from_another_backend()
    {
        var ncnn = LocalPriceFixture.At1080p("models-se:0", 2, 1.65, LocalPriceFixture.MachineKey,
            backend: UpscaleBackendPlan.NcnnVulkan);

        var hit = LocalPriceBook.Resolve(new[] { ncnn }, "models-se:0", 2, LocalPriceFixture.MachineKey, UpscaleBackendPlan.OnnxDml);
        Assert.False(hit.Usable);
        Assert.Equal(UpscaleBackendPlan.NcnnVulkan, hit.OtherBackend);

        // 同后端命中时:不报"另一后端"(Price 已经给出答案)
        Assert.Null(LocalPriceBook.Resolve(new[] { ncnn }, "models-se:0", 2, LocalPriceFixture.MachineKey,
            UpscaleBackendPlan.NcnnVulkan).OtherBackend);
        // 不传后端过滤(资料式提问)时也不报
        Assert.Null(LocalPriceBook.Resolve(new[] { ncnn }, "models-se:0", 2, LocalPriceFixture.MachineKey, null).OtherBackend);
        // 没有这一格 ⇒ 也没有"另一后端"
        Assert.Null(LocalPriceBook.Resolve(new[] { ncnn }, "models-se:0", 4, LocalPriceFixture.MachineKey,
            UpscaleBackendPlan.OnnxDml).OtherBackend);

        // 出处文本:另一后端的情形必须说清"已忽略"
        Assert.Null(LocalPriceBook.PerFrame1080p(new[] { ncnn }, "models-se:0", 2, out string prov,
            LocalPriceFixture.MachineKey, UpscaleBackendPlan.OnnxDml));
        Assert.Contains("另一后端", prov);
        Assert.Contains("已忽略", prov);
        Assert.Contains("ncnn-Vulkan", prov);
    }
}
