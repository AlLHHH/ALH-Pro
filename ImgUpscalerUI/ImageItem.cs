// ImageItem.cs — 列表项:图片路径 + 缩略图 + 显示名 + 可改名
// 实现 INotifyPropertyChanged:裁剪替换、改名等操作后 UI 自动刷新
using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ALHPro.Views;

public class ImageItem : INotifyPropertyChanged
{
    private string _path = "";
    private string _originalPath = "";
    private BitmapImage _thumb = new();
    private string _name = "";
    private string _info = "";
    private string _customName = "";

    public string Path
    {
        get => _path;
        set { if (_path != value) { _path = value; OnPropertyChanged(nameof(Path)); } }
    }

    /// <summary>用户添加时的原始路径(WebP 会自动转码,Path 会指向私有目录;输出默认目录用此值)。</summary>
    public string OriginalPath
    {
        get => _originalPath;
        set { if (_originalPath != value) { _originalPath = value; OnPropertyChanged(nameof(OriginalPath)); } }
    }

    public BitmapImage Thumb
    {
        get => _thumb;
        set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; OnPropertyChanged(nameof(Thumb)); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
    }

    public string Info
    {
        get => _info;
        set { if (_info != value) { _info = value; OnPropertyChanged(nameof(Info)); } }
    }

    public string CustomName
    {
        get => _customName;
        set { if (_customName != value) { _customName = value; OnPropertyChanged(nameof(CustomName)); } }
    }

    /// <summary>原图像素宽高。</summary>
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    // ---------- 处理状态(任务暂停后可删除"未处理"项,已处理/处理中的项不可删) ----------
    private int _progress;
    /// <summary>处理进度 0-100(0=未处理,100=已完成)。</summary>
    public int Progress
    {
        get => _progress;
        set { if (_progress != value) { _progress = value; OnPropertyChanged(nameof(Progress)); } }
    }

    private string _statusText = "";
    /// <summary>处理状态小字(等待处理/处理中/✓ 完成/✗ 失败),用于区分"未处理"与"已处理"。</summary>
    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(nameof(StatusText)); } }
    }

    /// <summary>是否还有未执行的任务(可用于判断暂停时能否删除)。</summary>
    public bool IsPending => Progress <= 0 && StatusText.Length == 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
