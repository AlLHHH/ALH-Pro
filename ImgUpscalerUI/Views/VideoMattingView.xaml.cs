using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ALHPro.Views;

/// <summary>视频抠图页(独立板块,2026-09-22)。
///
/// 【为什么是独立页面而不是并进图片抠图页】用户明确要求:"要的是单独一个界面 视频抠图,
/// 原先的还是图片抠图"。两页共享的只有模型注册表(`CutoutService.Models`)与参数语义,
/// 任务列表、素材类型、输出形态完全不同 —— 合在一页只会两边都别扭。
///
/// 【当前完成度】参数与默认值已按设计就位(模型默认 ISNET、容器默认 MOV/ProRes 4444、
/// 时序稳定默认 50),处理引擎尚未接线 —— 页面顶部有明确提示,按钮禁用,**不假装能用**。
/// 接线顺序见 docs/2026-09-22-video-matting-plan-2.md Task C(服务层)→ Task D Step 3(接 UI)。
/// </summary>
public sealed partial class VideoMattingView : UserControl
{
    /// <summary>界面参数(接线后交给 VideoMattingService;现在只用于显示与记忆)。</summary>
    public sealed class MattingParams
    {
        public string ModelKey { get; set; } = CutoutService.DefaultModelKey;
        public bool Transparent { get; set; }
        public string Container { get; set; } = "";
        public int Fg { get; set; } = 152;
        public int Bg { get; set; } = 84;
        public int Feather { get; set; } = 1;
        public int Edge { get; set; }
        public int Morph { get; set; } = 28;
        public int Stability { get; set; } = 50;
    }

    private readonly string[] _containers = AlhPro.Core.MattingOutputSpecs.TransparentContainers;

    /// <summary>XAML 是否已解析完。
    /// 【为什么必须有它】解析期 `IsChecked="True"` 就会触发 `Checked` 回调,那一刻其它控件还没建出来;
    /// 处理器里去摸 BgPanel/ContainerPanel 就是空引用,WinUI 把它报成
    /// `Failed to assign to property 'ToggleButton.IsChecked'` —— 页面整页加载失败(真机实测踩到过)。
    /// 修法不是删 XAML 里的默认值,而是让回调在解析期直接返回(与 CutoutView 的 _suppressEvents 同一思路)。</summary>
    private bool _ready;

    public VideoMattingView()
    {
        InitializeComponent();

        // 模型下拉:直接用抠图模型注册表(与图片抠图页同源,标签/顺序不再各写一份)
        foreach (var m in CutoutService.Models) ModelCombo.Items.Add(m.Label);
        ModelCombo.SelectedIndex = CutoutService.DefaultModelIndex;

        // 容器下拉:顺序即"默认档在前"(第一个 = MOV/ProRes 4444,实测到处都认);显示名带取舍说明
        foreach (var c in _containers) ContainerCombo.Items.Add(ContainerLabel(c));
        ContainerCombo.SelectedIndex = 0;

        ApplyModelPresets();
        UpdateLabels();
        UpdateModeVisibility();
        _ready = true;   // 解析期回调到此为止都忽略;之后用户操作才生效
    }

    /// <summary>容器 key → 界面名。文案里带上体积/兼容性的取舍,别让用户盲选。</summary>
    private static string ContainerLabel(string key) => key switch
    {
        "mov" => "MOV · ProRes 4444(推荐,兼容性最好,体积大)",
        "webm" => "WebM · VP9-alpha(体积小,部分软件不认透明)",
        _ => key,
    };

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;   // 构造期设置 SelectedIndex 也会触发,那时不需要(也不该)重设滑条默认值
        ApplyModelPresets();
    }

    private void OutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;   // 解析期 IsChecked="True" 会先触发一次
        UpdateModeVisibility();
    }

    /// <summary>滑条拖动即时更新标签(否则标签停在旧值,看着像"滑条没反应")。</summary>
    private void Param_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateLabels();
    }

    private void ApplyModelPresets()
    {
        int i = Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1);
        var m = CutoutService.Models[i];
        FgSlider.Value = m.FgPreset;
        BgSlider.Value = m.BgPreset;
        FeatherSlider.Value = m.FeatherPreset;
        EdgeSlider.Value = m.EdgePreset;
        MorphSlider.Value = m.MorphPreset;
        ModelHint.Text = m.InputSize >= 1024
            ? "该模型输入 1024×1024:边缘更细,但单帧更慢;若本机 GPU 不可用会自动改 CPU。"
            : "该模型输入 320×320:最快,适合长视频;细节略弱于 ISNet。";
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        FgLabel.Text = $"前景阈值 {FgSlider.Value:0}";
        BgLabel.Text = $"背景阈值 {BgSlider.Value:0}";
        FeatherLabel.Text = $"边缘羽化 {FeatherSlider.Value:0}";
        EdgeLabel.Text = $"边缘增强 {EdgeSlider.Value:0}";
        MorphLabel.Text = $"形态学清洗 {MorphSlider.Value:0}";
        StabilityLabel.Text = $"稳定强度 {StabilitySlider.Value:0}";
    }

    private void UpdateModeVisibility()
    {
        bool transparent = ModeAlphaRadio.IsChecked == true;
        BgPanel.Visibility = transparent ? Visibility.Collapsed : Visibility.Visible;
        ContainerPanel.Visibility = transparent ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把界面上的选择收成一个参数对象(接线后直接喂给服务层)。</summary>
    public MattingParams Collect() => new()
    {
        ModelKey = CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)].Key,
        Transparent = ModeAlphaRadio.IsChecked == true,
        Container = _containers[Math.Clamp(ContainerCombo.SelectedIndex, 0, _containers.Length - 1)],
        Fg = (int)FgSlider.Value,
        Bg = (int)BgSlider.Value,
        Feather = (int)FeatherSlider.Value,
        Edge = (int)EdgeSlider.Value,
        Morph = (int)MorphSlider.Value,
        Stability = (int)StabilitySlider.Value,
    };
}
