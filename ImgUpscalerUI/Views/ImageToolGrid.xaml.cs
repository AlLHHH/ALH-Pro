// ImageToolGrid.xaml.cs — 图片列表控件:鼠标框选多选、铅笔操作面板(改名/裁剪/详细信息)、批量改名
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ALHPro.Views;

public sealed partial class ImageToolGrid : UserControl
{
    private bool _rubberBanding;
    private bool _suppressSelection;
    private Point _rbStart;
    private ImageItem? _pencilItem;
    private ImageItem? _cropItem;   // 裁剪目标(独立于 _pencilItem,隐藏面板不影响)

    // ---- 裁剪状态 ----
    private bool _cropDragging;
    private Point _cropStart;
    private Point _cropEnd;
    private (double w, double h)? _cropImgSize;

    public ObservableCollection<ImageItem> Items { get; } = new();

    /// <summary>当前选中的图片(按选中顺序)。</summary>
    public IReadOnlyList<ImageItem> SelectedItems
        => ImageGrid.SelectedItems.OfType<ImageItem>().ToList();

    /// <summary>双击缩略图(打开大图预览)。</summary>
    public event Action<ImageItem>? ItemDoubleTapped;

    /// <summary>选中变化事件(参数:当前选中的图片)。</summary>
    public event Action<IReadOnlyList<ImageItem>>? SelectionChanged;

    /// <summary>区域放大请求(裁剪浮层按钮;仅图片放大页接线,参数=选区像素坐标)。</summary>
    public event Action<ImageItem, int, int, int, int>? RegionUpscaleRequested;

    /// <summary>是否显示"放大选区"按钮(图片放大页开启,抠图页隐藏)。</summary>
    public bool RegionUpscaleEnabled
    {
        get => _regionUpscaleEnabled;
        set { _regionUpscaleEnabled = value; if (RegionUpscaleBtn != null) RegionUpscaleBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }
    private bool _regionUpscaleEnabled;

    public ImageToolGrid()
    {
        this.InitializeComponent();
        ImageGrid.ItemsSource = Items;
        Items.CollectionChanged += (_, _) => UpdateListState();
        UpdateListState();
        // Del 快捷键:焦点在网格区域内时按 Del 删除选中(不依赖焦点在具体控件上,最可靠)
        var delAcc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Delete, ScopeOwner = GridHost };
        delAcc.Invoked += (_, e) =>
        {
            if (ImageGrid.SelectedItems.Count > 0)
                RemoveBtn_Click(this, new RoutedEventArgs());
            e.Handled = true;
        };
        GridHost.KeyboardAccelerators.Add(delAcc);
    }

    // ---------- 列表管理 ----------
    public async Task AddImagesAsync(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var path = p;
            // WebP 引擎无法解码,自动转码为 PNG(存应用私有目录,启动自动清理)
            if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    path = await ConvertWebpToPngAsync(path);
                }
                catch { continue; }   // 转码失败(文件损坏等)跳过
            }
            if (Items.Any(i => i.Path == path)) continue;
            var fi = new FileInfo(path);
            var item = new ImageItem
            {
                Path = path,
                OriginalPath = p,
                Name = fi.Name,
                Info = $"{fi.Length / 1024} KB",
            };
            try
            {
                using var b = new System.Drawing.Bitmap(path);
                item.PixelWidth = b.Width;
                item.PixelHeight = b.Height;
                // 手机照片 EXIF 旋转修正:预览按旋转后方向显示,尺寸基准必须一致(否则框选/涂抹坐标错位)
                foreach (System.Drawing.Imaging.PropertyItem pi in b.PropertyItems)
                {
                    if (pi.Id == 0x0112 && pi.Value is { Length: > 0 } && pi.Value[0] is 6 or 8)
                    {
                        (item.PixelWidth, item.PixelHeight) = (item.PixelHeight, item.PixelWidth);
                        break;
                    }
                }
            }
            catch { }
            try { item.Thumb = new BitmapImage(new Uri(path)); } catch (Exception) { }
            Items.Add(item);
        }
        UpdateListState();
    }

    /// <summary>用 WinRT 图像解码器把 WebP 转码为 PNG(引擎不支持 WebP)。</summary>
    private static async Task<string> ConvertWebpToPngAsync(string webpPath)
    {
        var outPath = UpscaleView.UniquePath(CroppedStorage.Dir,
            Path.GetFileNameWithoutExtension(webpPath) + ".png");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(webpPath);
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync();
        using var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, memStream);
        encoder.SetPixelData(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode,
            decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync();
        memStream.Seek(0);
        using var fs = File.Create(outPath);
        await memStream.AsStreamForRead().CopyToAsync(fs);
        return outPath;
    }

    /// <summary>处理进行中:锁定删除/清空/改名等列表操作(防止处理中删掉正在处理的项)。</summary>
    private bool _processing;
    public bool IsProcessing
    {
        set { _processing = value; UpdateListState(); }
    }

    /// <summary>任务已暂停:允许删除"未处理"的项(已处理/处理中的项仍不可删)。</summary>
    private bool _paused;
    public bool IsPaused
    {
        set { _paused = value; UpdateListState(); }
    }

    private void UpdateListState()
    {
        var n = Items.Count;
        var sel = ImageGrid.SelectedItems.OfType<ImageItem>().ToList();
        ListCount.Text = n > 0
            ? (sel.Count > 0 ? $"共 {n} 张 · 已选 {sel.Count}" : $"共 {n} 张")
            : "";
        // 处理中锁死:按钮始终显示(选中/有图片才亮),处理中置灰点不了(锁死但不藏起来);
        // 暂停时解锁「删除」(只删未处理项),清空/批量改名仍锁
        ClearSelBtn.IsEnabled = !_processing && sel.Count > 0;
        ClearSelBtn.Visibility = sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchRenameBtn.IsEnabled = !_processing && sel.Count > 0;
        BatchRenameBtn.Visibility = sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveBtn.IsEnabled = (!_processing || _paused) && sel.Count > 0;
        // 空列表(无真实图片 n==0)绝不显示:WinUI 幽灵项会误报选中,导致这个"删除提示"悬停浮现
        RemoveBtn.Visibility = sel.Count > 0 && n > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearBtn.IsEnabled = !_processing && n > 0;
        ClearBtn.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        DropHint.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        // 注意:不能设 GridHost.IsHitTestVisible=false——那会连拖放一起禁用(列表空时拖不进)。
        // WinUI 空列表幽灵项(悬停浮出模板元素)改用模板内按钮默认隐藏来解决,见 ItemTemplate。
        // (视频项 ReRunBtnVisibility 已加 FallbackValue=Collapsed;此处保持 GridHost 可交互)
        UpdateItemSelectionVisuals();   // 选中视觉始终同步(不受 _suppressSelection 影响)
        if (!_suppressSelection)
            SelectionChanged?.Invoke(sel);
    }

    /// <summary>直接控制每个容器模板内的选中视觉(蓝底+蓝框),不依赖 VisualState。</summary>
    private void UpdateItemSelectionVisuals()
    {
        var selected = ImageGrid.SelectedItems.OfType<ImageItem>().ToHashSet();
        for (int i = 0; i < Items.Count; i++)
        {
            if (ImageGrid.ContainerFromIndex(i) is not GridViewItem container) continue;
            ApplySelectionVisual(container, selected.Contains(Items[i]));
        }
    }

    private static void ApplySelectionVisual(GridViewItem container, bool isSelected)
    {
        var bg = container.FindName("SelectedBg") as Border;
        var border = container.FindName("SelectionBorder") as Border;
        if (bg != null)
            bg.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        if (border != null)
            border.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>容器内容变化(首次实例化/滚动回收)时应用当前选中视觉。</summary>
    private void ImageGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is ImageItem item && args.ItemContainer is GridViewItem container)
        {
            var isSelected = ImageGrid.SelectedItems.Contains(item);
            ApplySelectionVisual(container, isSelected);
        }
    }

    /// <summary>取消选择:清空全部选中。</summary>
    private void ClearSelBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        try { ImageGrid.SelectedItems.Clear(); }
        finally { _suppressSelection = false; }
        UpdateListState();
    }

    // Del 键删除选中的图片(网格获得焦点时)
    private void ImageGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_processing && !_paused) return;   // 处理中(未暂停)锁死;暂停时放行,由 RemoveBtn_Click 过滤
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            if (ImageGrid.SelectedItems.Count > 0)
                RemoveBtn_Click(sender, e);
        }
    }

    // 框选画布根级 Del 监听:橡皮筋框选后焦点不在网格上,Del 也能删除选中项
    private void GridHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_processing && !_paused) return;   // 处理中(未暂停)锁死
        if (e.Handled) return;   // 网格自身已处理
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            if (ImageGrid.SelectedItems.Count > 0)
                RemoveBtn_Click(sender, e);
        }
    }

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = ImageGrid.SelectedItems.OfType<ImageItem>().ToList();
        if (selected.Count == 0) return;
        if (_processing)
        {
            // 处理中:必须先暂停才能删除,且只能删「未处理」的项
            if (!_paused)
            {
                await ShowInfoAsync("任务处理中,需先暂停才能删除未执行的项目。\n(暂停后只允许删除还没处理的项目)");
                return;
            }
            var pending = selected.Where(it => it.IsPending).ToList();
            var blocked = selected.Where(it => !it.IsPending).ToList();
            if (pending.Count == 0)
            {
                await ShowInfoAsync("选中的项目已处理或正在处理,不能删除;\n只能删除还没处理的项目(暂停状态下)。");
                return;
            }
            foreach (var item in pending) Items.Remove(item);
            HidePencilPanel();
            UpdateListState();
            AppLogger.Info($"删除了 {pending.Count} 张图片(处理中暂停,只删未处理项)");
            if (blocked.Count > 0)
                await ShowInfoAsync($"已删除 {pending.Count} 个未处理的项目;\n其余 {blocked.Count} 个已处理/处理中的项目不能删除。");
            return;
        }
        foreach (var item in selected) Items.Remove(item);
        HidePencilPanel();
        UpdateListState();
        AppLogger.Info($"删除了 {selected.Count} 张图片(列表剩 {Items.Count} 张)");
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_processing) { return; }   // 处理中锁死,不允许清空
        int n = Items.Count;
        Items.Clear();
        HidePencilPanel();
        UpdateListState();
        if (n > 0) AppLogger.Info($"清空了图片列表(共 {n} 张)");
    }

    // ---------- 鼠标框选(橡皮筋) ----------
    // 注意:指针事件挂在 GridHost 上,与 RbRect 同坐标系,框选矩形从按下点到当前点精确对应
    private const double RbThreshold = 4;   // 移动超过该距离视为拖拽框选,否则视为单击
    private bool _rbMoved;

    private void GridHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 任意位置(含图片上)按下都可开始;单击/拖拽在释放时区分
        // 注意:不立即捕获指针,避免破坏 GridView 单击选中/按钮点击;判定为拖拽后再捕获
        _rubberBanding = true;
        _rbMoved = false;
        _rbStart = e.GetCurrentPoint(GridHost).Position;
        RbRect.Visibility = Visibility.Visible;
        RbRect.Width = 0;
        RbRect.Height = 0;
        Canvas.SetLeft(RbRect, _rbStart.X);
        Canvas.SetTop(RbRect, _rbStart.Y);
    }

    private void GridHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_rubberBanding) return;
        var cur = e.GetCurrentPoint(GridHost).Position;
        if (!_rbMoved &&
            Math.Abs(cur.X - _rbStart.X) < RbThreshold &&
            Math.Abs(cur.Y - _rbStart.Y) < RbThreshold)
            return; // 尚未构成拖拽
        if (!_rbMoved)
        {
            _rbMoved = true;
            GridHost.CapturePointer(e.Pointer);   // 确认拖拽后才捕获,单击/按钮点击不受影响
        }
        UpdateRbRect(cur);
    }

    private void GridHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_rubberBanding) return;
        _rubberBanding = false;
        GridHost.ReleasePointerCapture(e.Pointer);
        var cur = e.GetCurrentPoint(GridHost).Position;
        if (_rbMoved)
        {
            UpdateRbRect(cur);
            ApplyRubberSelection();
            // 框选后聚焦到框选画布(设为可聚焦),Del 键直接可删;延迟一帧确保焦点切换生效
            DispatcherQueue.TryEnqueue(() => GridHost.Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
        }
        else
        {
            // 单击:点在空白处(不在任何缩略图上)→ 清空选择
            bool onItem = false;
            for (int i = 0; i < Items.Count; i++)
            {
                if (ImageGrid.ContainerFromIndex(i) is FrameworkElement c)
                {
                    var tf = c.TransformToVisual(GridHost);
                    var tl = tf.TransformPoint(new Windows.Foundation.Point(0, 0));
                    var r = new Windows.Foundation.Rect(tl.X, tl.Y, c.ActualWidth, c.ActualHeight);
                    if (r.Contains(cur)) { onItem = true; break; }
                }
            }
            if (!onItem)
            {
                _suppressSelection = true;
                try { ImageGrid.SelectedItems.Clear(); }
                finally { _suppressSelection = false; }
                UpdateListState();
            }
        }
        // 未拖拽(单击)时图片上的默认选择交给 GridView 处理
        RbRect.Visibility = Visibility.Collapsed;
    }

    private void GridHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _rubberBanding = false;
        RbRect.Visibility = Visibility.Collapsed;
    }

    private void UpdateRbRect(Point cur)
    {
        double x = Math.Min(_rbStart.X, cur.X);
        double y = Math.Min(_rbStart.Y, cur.Y);
        Canvas.SetLeft(RbRect, x);
        Canvas.SetTop(RbRect, y);
        RbRect.Width = Math.Abs(cur.X - _rbStart.X);
        RbRect.Height = Math.Abs(cur.Y - _rbStart.Y);
        RbRect.Visibility = RbRect.Width > 2 && RbRect.Height > 2
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>根据框选矩形与可见项容器相交关系设置多选。
    /// 坐标统一用 GridHost(与框选矩形同坐标系)。</summary>
    private void ApplyRubberSelection()
    {
        var rect = new Rect(Canvas.GetLeft(RbRect), Canvas.GetTop(RbRect),
            RbRect.Width, RbRect.Height);
        _suppressSelection = true;
        try
        {
            ImageGrid.SelectedItems.Clear();
            if (rect.Width >= 2 && rect.Height >= 2)
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (ImageGrid.ContainerFromIndex(i) is not GridViewItem container) continue;
                    var pos = container.TransformToVisual(GridHost).TransformPoint(new Point(0, 0));
                    var itemRect = new Rect(pos.X, pos.Y, container.ActualWidth, container.ActualHeight);
                    if (RectIntersects(rect, itemRect))
                        ImageGrid.SelectedItems.Add(Items[i]);
                }
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        UpdateListState();
    }

    /// <summary>两个矩形的相交判断(Windows.Foundation.Rect 无 IntersectsWith)。</summary>
    private static bool RectIntersects(Rect a, Rect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X &&
           a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private void ImageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateListState();

    // ---------- 悬停铅笔 ----------
    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // 悬停只显示铅笔;选中高亮由容器样式(SelectionBorder)负责,不再改缩略图边框
        if (sender is FrameworkElement root && root.FindName("PencilBtn") is Button b)
            b.Opacity = 1;
    }

    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement root && root.FindName("PencilBtn") is Button b)
            b.Opacity = 0;
    }

    // ---------- 铅笔面板 ----------
    private void PencilBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ImageItem item) return;
        // 再次点击同一张的铅笔 → 收回
        if (_pencilItem == item && PencilPanel.Visibility == Visibility.Visible)
        {
            HidePencilPanel();
            return;
        }
        ShowPencilPanel(item);
    }

    private async void ShowPencilPanel(ImageItem item)
    {
        _pencilItem = item;
        PencilPanel.Visibility = Visibility.Visible;
        InfoName.Text = item.Name;
        InfoResolution.Text = "读取中...";
        InfoSize.Text = "—";
        InfoFormat.Text = "—";
        InfoBitDepth.Text = "—";
        InfoColorSpace.Text = "—";
        InfoAvgRgb.Text = "—";
        InfoLuma.Text = "—";
        // 详细信息在后台线程计算,避免大图卡 UI;任何异常都不影响面板显示
        var path = item.Path;
        ImageInfo? info;
        try
        {
            info = await Task.Run(() => ImageInfoService.GetInfo(path));
        }
        catch (Exception)
        {
            info = null;
        }
        if (_pencilItem != item || info is null) return; // 面板已切换或读取失败
        InfoResolution.Text = info.Resolution;
        InfoSize.Text = info.FileSize;
        InfoFormat.Text = info.Format;
        InfoBitDepth.Text = info.BitDepth;
        InfoColorSpace.Text = info.ColorSpace;
        InfoAvgRgb.Text = info.AvgRgb;
        InfoLuma.Text = info.Luma;
    }

    private void HidePencilPanel()
    {
        _pencilItem = null;
        PencilPanel.Visibility = Visibility.Collapsed;
    }

    private void PencilCloseBtn_Click(object sender, RoutedEventArgs e) => HidePencilPanel();

    private async void RenameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pencilItem is not ImageItem item) return;
        await RenameItemAsync(item);
    }

    private async Task RenameItemAsync(ImageItem item)
    {
        var box = new TextBox
        {
            Text = !string.IsNullOrWhiteSpace(item.CustomName)
                ? item.CustomName
                : Path.GetFileNameWithoutExtension(item.Path),
        };
        var dlg = new ContentDialog
        {
            Title = "设置输出文件名",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length > 0)
        {
            // 输出名去重:其他图片已用该名时,自动追加 1、2、3...
            var taken = Items.Where(i => i != item)
                .Select(i => i.CustomName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet();
            var final = name;
            int n = 1;
            while (taken.Contains(final)) final = name + n++;
            item.CustomName = final;
            item.Name = final + " (自定义)";
            if (final != name)
                await ShowInfoAsync($"输出名 \"{name}\" 已被其他图片使用,\n已自动改为 \"{final}\"。");
        }
        else
        {
            item.CustomName = "";
            item.Name = Path.GetFileName(item.Path);
        }
        if (_pencilItem == item) InfoName.Text = item.Name;
    }

    // ---------- 批量改名(wo, wo1, wo2...;自动避开其他图片已用的输出名) ----------
    private async void BatchRenameBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = ImageGrid.SelectedItems.OfType<ImageItem>().ToList();
        if (selected.Count == 0)
        {
            await ShowInfoAsync("请先框选要改名的图片");
            return;
        }
        var box = new TextBox { PlaceholderText = "如: wo" };
        var dlg = new ContentDialog
        {
            Title = $"批量改名(共 {selected.Count} 张)",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0) return;

        // 已占用名字 = 未选中图片的输出名(选中图片之间按序号分配)
        var used = Items.Where(i => !selected.Contains(i))
            .Select(i => i.CustomName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet();

        // 按列表顺序命名:第 1 张 name,后续 name1, name2...;冲突时自动后移序号
        int idx = 0;
        var assigned = new List<string>();
        foreach (var item in Items.Where(i => selected.Contains(i)).ToList())
        {
            string candidate;
            if (idx == 0)
            {
                candidate = name;
                if (used.Contains(candidate))
                {
                    int n = 1;
                    while (used.Contains(name + n)) n++;
                    candidate = name + n;
                }
            }
            else
            {
                int n = idx;
                while (used.Contains(name + n)) n++;
                candidate = name + n;
            }
            used.Add(candidate);
            assigned.Add(candidate);
            item.CustomName = candidate;
            item.Name = candidate + " (自定义)";
            idx++;
        }
        if (_pencilItem is not null && selected.Contains(_pencilItem))
            InfoName.Text = _pencilItem.Name;
        var preview = string.Join(", ", assigned.Take(4)) + (assigned.Count > 4 ? " ..." : "");
        await ShowInfoAsync($"已批量改名 {assigned.Count} 张:\n{preview}");
    }

    // ---------- 双击缩略图 → 大图预览 ----------
    private void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 双击的是铅笔按钮(操作面板)时不打开预览
        if (IsInsidePencil(e.OriginalSource as DependencyObject)) return;
        if ((sender as FrameworkElement)?.DataContext is ImageItem item)
            ItemDoubleTapped?.Invoke(item);
    }

    private static bool IsInsidePencil(DependencyObject? o)
    {
        while (o != null)
        {
            if (o is Button b && b.Name == "PencilBtn") return true;
            o = VisualTreeHelper.GetParent(o);
        }
        return false;
    }

    // ---------- 裁剪(替换预览,原文件保留) ----------
    private void CropBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pencilItem is null) return;
        _cropItem = _pencilItem;
        PencilPanel.Visibility = Visibility.Collapsed;   // 只隐藏面板,不清引用
        CropOverlay.Visibility = Visibility.Visible;
        CropConfirmBtn.Content = "确认裁剪";
        CropInfoText.Text = "在图片上拖拽框选裁剪区域";
        _cropImgSize = null;
        _cropDragging = false;
        CropRect.Visibility = Visibility.Collapsed;
        CropCanvas.Visibility = Visibility.Visible;
        CropConfirmBtn.IsEnabled = false;
        Canvas.SetLeft(CropInfo, 8);
        Canvas.SetTop(CropInfo, 8);
        var bmp = new BitmapImage(new Uri(_cropItem.Path));
        bmp.ImageOpened += (_, _) =>
            _cropImgSize = (bmp.PixelWidth, bmp.PixelHeight);
        CropImage.Source = bmp;
    }

    private void CropCancel_Click(object sender, RoutedEventArgs e)
    {
        CropOverlay.Visibility = Visibility.Collapsed;
        if (_cropItem is not null) ShowPencilPanel(_cropItem);
        _cropItem = null;
    }

    private void CropCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(CropCanvas).Position;
        if (CanvasToPixel(pt) is null) return;
        _cropDragging = true;
        _cropStart = pt;
        _cropEnd = pt;
        CropCanvas.CapturePointer(e.Pointer);
    }

    private void CropCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropDragging) return;
        _cropEnd = e.GetCurrentPoint(CropCanvas).Position;
        double x = Math.Min(_cropStart.X, _cropEnd.X);
        double y = Math.Min(_cropStart.Y, _cropEnd.Y);
        double w = Math.Abs(_cropEnd.X - _cropStart.X);
        double h = Math.Abs(_cropEnd.Y - _cropStart.Y);
        Canvas.SetLeft(CropRect, x);
        Canvas.SetTop(CropRect, y);
        CropRect.Width = w;
        CropRect.Height = h;
        CropRect.Visibility = w > 1 && h > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (GetSelPixelRect() is (int px, int py, int pw, int ph))
        {
            CropInfoText.Text = $"{pw} × {ph} px  (起点 {px},{py})";
            Canvas.SetLeft(CropInfo, Math.Max(0, x));
            Canvas.SetTop(CropInfo, Math.Max(0, y - 26));
        }
    }

    private void CropCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_cropDragging) return;
        _cropDragging = false;
        CropCanvas.ReleasePointerCapture(e.Pointer);
        CropConfirmBtn.IsEnabled =
            GetSelPixelRect() is (_, _, var w, var h) && w >= 8 && h >= 8;
        if (!CropConfirmBtn.IsEnabled)
            CropInfoText.Text = "选区过小(至少 8×8 像素),请重新框选";
    }

    private void CropCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _cropDragging = false;

    /// <summary>画布坐标 → 图片像素坐标;null 表示在图片绘制区外。</summary>
    private (double px, double py)? CanvasToPixel(Point pt)
    {
        if (_cropImgSize is not (var iw, var ih) || iw <= 0 || ih <= 0) return null;
        double aw = CropImage.ActualWidth, ah = CropImage.ActualHeight;
        if (aw <= 0 || ah <= 0) return null;
        double s = Math.Min(aw / iw, ah / ih);
        double ox = (aw - iw * s) / 2, oy = (ah - ih * s) / 2;
        double px = (pt.X - ox) / s, py = (pt.Y - oy) / s;
        if (px < 0 || py < 0 || px >= iw || py >= ih) return null;
        return (px, py);
    }

    private (int x, int y, int w, int h)? GetSelPixelRect()
    {
        if (_cropImgSize is not (var iw, var ih) || iw <= 0 || ih <= 0) return null;
        double aw = CropImage.ActualWidth, ah = CropImage.ActualHeight;
        if (aw <= 0 || ah <= 0) return null;
        double s = Math.Min(aw / iw, ah / ih);
        double ox = (aw - iw * s) / 2, oy = (ah - ih * s) / 2;
        int x = (int)Math.Floor((Math.Min(_cropStart.X, _cropEnd.X) - ox) / s);
        int y = (int)Math.Floor((Math.Min(_cropStart.Y, _cropEnd.Y) - oy) / s);
        int x2 = (int)Math.Ceiling((Math.Max(_cropStart.X, _cropEnd.X) - ox) / s);
        int y2 = (int)Math.Ceiling((Math.Max(_cropStart.Y, _cropEnd.Y) - oy) / s);
        x = Math.Clamp(x, 0, (int)iw);
        y = Math.Clamp(y, 0, (int)ih);
        x2 = Math.Clamp(x2, 0, (int)iw);
        y2 = Math.Clamp(y2, 0, (int)ih);
        int w = x2 - x, h = y2 - y;
        if (w <= 0 || h <= 0) return null;
        return (x, y, w, h);
    }

    // 放大选区:把框选区域交给页面(图片放大页)做 AI 放大
    private async void RegionUpscale_Click(object sender, RoutedEventArgs e)
    {
        if (_cropItem is null) return;
        if (GetSelPixelRect() is not (int x, int y, int w, int h) || w < 8 || h < 8)
        {
            await ShowInfoAsync("选区过小(至少 8×8 像素)");
            return;
        }
        if (RegionUpscaleRequested == null)
        {
            await ShowInfoAsync("当前页面不支持区域放大");
            return;
        }
        var item = _cropItem;
        CropOverlay.Visibility = Visibility.Collapsed;
        _cropItem = null;
        RegionUpscaleRequested.Invoke(item, x, y, w, h);
    }

    private async void CropConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_cropItem is null) return;
        if (GetSelPixelRect() is not (int x, int y, int w, int h) || w < 8 || h < 8)
        {
            await ShowInfoAsync("选区过小(至少 8×8 像素)");
            return;
        }
        var item = _cropItem;
        try
        {
            // 裁剪:产物保存到应用私有目录(不污染源图目录/桌面),仅右侧列表使用
            var newPath = UpscaleView.UniquePath(CroppedStorage.Dir,
                Path.GetFileNameWithoutExtension(item.Path) + "_裁剪.png");
            CropConfirmBtn.IsEnabled = false;
            CropInfoText.Text = "正在裁剪...";
            await Task.Run(() =>
            {
                using var src = new System.Drawing.Bitmap(item.Path);
                using var cropped = src.Clone(
                    new System.Drawing.Rectangle(x, y, w, h), src.PixelFormat);
                cropped.Save(newPath, System.Drawing.Imaging.ImageFormat.Png);
            });

            // 替换预览(原文件保留),静默完成,不弹提示
            item.Path = newPath;
            item.Name = Path.GetFileName(newPath) + " (裁剪)";
            item.CustomName = "";
            item.Info = $"裁剪 {w}×{h}";
            try { item.Thumb = new BitmapImage(new Uri(newPath)); } catch (Exception) { }

            CropOverlay.Visibility = Visibility.Collapsed;
            ShowPencilPanel(item);
        }
        catch (Exception ex)
        {
            CropInfoText.Text = "裁剪失败: " + ex.Message;
            await ShowErrorAsync("裁剪失败: " + ex.Message);
        }
        finally
        {
            _cropItem = null;
        }
    }

    // ---------- 拖拽添加 ----------
    public static bool IsImageExt(string ext)
        => ext.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";

    private void DropBorder_DragOver(object sender, DragEventArgs e)
        => e.AcceptedOperation = DataPackageOperation.Copy;

    private async void DropBorder_Drop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // 必须 await(不能 .Result)——UI 线程同步等待会死锁,表现为"拖不进去"
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<Windows.Storage.StorageFile>()
                .Where(f => IsImageExt(Path.GetExtension(f.Path)))
                .Select(f => f.Path);
            await AddImagesAsync(paths);
        }
    }

    // ---------- 对话框 ----------
    private async Task ShowInfoAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "提示",
            Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "确定",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private async Task ShowErrorAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "错误",
            Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
