using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace SysMonitor;

/// <summary>
/// 编辑任务栏上显示哪些项目。勾选的变化立即通过 apply 应用到任务栏，窗口里的预览也用任务栏同一套代码绘制；
/// 点“确定”才保存，取消或直接关闭则恢复打开时的设置。
/// </summary>
sealed partial class TaskbarEditor : Window
{
    readonly TaskbarItems _original;
    readonly Action<TaskbarItems> _apply;
    readonly Dictionary<CheckBox, Func<BarValues, float?>> _sources;
    TaskbarItems _items;
    BarValues? _values;
    bool _syncing, _saved;

    /// <param name="apply">勾选变化时调用（实时预览）；关闭时再调用一次，传入最终结果（确定）或原设置（取消）</param>
    /// <param name="displayed">任务栏数据当前是否开着，关着时提示一下预览只在这个窗口里</param>
    public TaskbarEditor(TaskbarItems items, BarValues? values, bool displayed, Action<TaskbarItems> apply)
    {
        InitializeComponent();
        _original = _items = items;
        _values = values;
        _apply = apply;
        _sources = new()
        {
            [CpuLoadBox] = v => v.CpuLoad,
            [CpuTempBox] = v => v.CpuTemp,
            [MemLoadBox] = v => v.MemLoad,
            [GpuLoadBox] = v => v.GpuLoad,
            [GpuTempBox] = v => v.GpuTemp,
            [GpuMemLoadBox] = v => v.GpuMemLoad,
            [NetUpBox] = v => v.Up,
            [NetDownBox] = v => v.Down,
        };
        HintText.Text = !TaskbarPanel.Supported ? "任务栏数据需要 Windows 11。"
            : displayed ? "修改会立即显示在任务栏上；任务栏空间不够时会从右边的块开始省略。"
            : "任务栏数据当前未开启，可在“查看 → 任务栏数据”里选择显示位置。";
        SyncBoxes();
        Loaded += (_, _) => RenderPreview();
        DpiChanged += (_, _) => RenderPreview();
    }

    /// <summary>传感器每次刷新时调用，预览跟着变。</summary>
    public void UpdateValues(BarValues values)
    {
        _values = values;
        UpdateLabels();
        RenderPreview();
    }

    void SyncBoxes()
    {
        _syncing = true;
        CpuLoadBox.IsChecked = _items.CpuLoad;
        CpuTempBox.IsChecked = _items.CpuTemp;
        MemLoadBox.IsChecked = _items.MemLoad;
        GpuLoadBox.IsChecked = _items.GpuLoad;
        GpuTempBox.IsChecked = _items.GpuTemp;
        GpuMemLoadBox.IsChecked = _items.GpuMemLoad;
        NetUpBox.IsChecked = _items.NetUp;
        NetDownBox.IsChecked = _items.NetDown;
        MetersBox.IsChecked = _items.Meters;
        _syncing = false;
        UpdateLabels();
    }

    /// <summary>这台电脑读不到的项目在名字后注明，免得勾上了却奇怪为什么不显示。</summary>
    void UpdateLabels()
    {
        foreach (var (box, source) in _sources)
        {
            var label = (string)box.Tag;
            box.Content = _values is BarValues v && source(v) == null ? label + "（无数据）" : label;
        }
    }

    void Item_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        _items = new TaskbarItems
        {
            CpuLoad = CpuLoadBox.IsChecked == true,
            CpuTemp = CpuTempBox.IsChecked == true,
            MemLoad = MemLoadBox.IsChecked == true,
            GpuLoad = GpuLoadBox.IsChecked == true,
            GpuTemp = GpuTempBox.IsChecked == true,
            GpuMemLoad = GpuMemLoadBox.IsChecked == true,
            NetUp = NetUpBox.IsChecked == true,
            NetDown = NetDownBox.IsChecked == true,
            Meters = MetersBox.IsChecked == true,
        };
        _apply(_items);
        RenderPreview();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _items = new TaskbarItems();
        SyncBoxes();
        _apply(_items);
        RenderPreview();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _saved = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _apply(_saved ? _items : _original);
    }

    void RenderPreview()
    {
        if (!IsLoaded)
            return;
        bool light = TaskbarPanel.SystemUsesLightTheme();
        // 近似 Win11 任务栏的底色
        PreviewHost.Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xF1, 0xF5) : Color.FromRgb(0x1C, 0x1C, 0x1C));

        if (_values is not BarValues values)
        {
            ShowEmpty("正在等待传感器数据…");
            return;
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        using var bmp = TaskbarPanel.RenderPreview(values, _items, (int)Math.Round(dpi), light);
        if (bmp == null)
        {
            ShowEmpty(_sources.Keys.Any(b => b.IsChecked == true)
                ? "勾选的项目在这台电脑上都读不到数据" : "没有选择任何项目，任务栏上将不显示数据");
            return;
        }
        PreviewImage.Source = ToBitmapSource(bmp, dpi);
        PreviewImage.Visibility = Visibility.Visible;
        PreviewEmpty.Visibility = Visibility.Collapsed;
    }

    void ShowEmpty(string text)
    {
        PreviewEmpty.Text = text;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewEmpty.Visibility = Visibility.Visible;
    }

    /// <summary>GDI+ 的预乘透明位图转成 WPF 图片；按实际 DPI 标注，显示时一个像素对一个像素。</summary>
    static BitmapSource ToBitmapSource(Drawing.Bitmap bmp, double dpi)
    {
        var rect = new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var source = BitmapSource.Create(bmp.Width, bmp.Height, dpi, dpi, PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
