using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Shapes = System.Windows.Shapes;

namespace EagleHammerMonitor;

/// <summary>
/// 编辑任务栏布局：拖动项目调整位置（左右换列、上下换行、拖到“未显示”隐藏），点击项目设置这一行显示哪些数据。
/// 每次改动立即通过 apply 应用到任务栏，窗口里的预览也用任务栏同一套代码绘制；
/// 点“确定”才保存，取消或直接关闭则恢复打开时的设置。
/// </summary>
sealed partial class TaskbarEditor : Window
{
    const double SlotWidth = 128, SlotHeight = 36, SlotGap = 6, ColumnGap = 10;

    /// <summary>拖放目标：某列某行；Col 为 -1 表示“未显示”区。</summary>
    readonly record struct Target(int Col, int Row);

    readonly TaskbarLayout _original;
    readonly Action<TaskbarLayout> _apply;
    TaskbarLayout _layout;
    SensorSnapshot? _snapshot;
    TaskbarRowKind _selected;
    bool _syncing, _saved;

    // 下方“显示内容”里的勾选框，传感器刷新时更新后面的当前值
    readonly List<(CheckBox Box, string Label, Func<SensorSnapshot, string?> Value)> _detailBoxes = [];
    string _fanIds = "";

    // 拖动状态：按下的项目、按下位置、鼠标在卡片里的位置；超过系统拖动阈值才算开始拖
    readonly List<(FrameworkElement Slot, Target Target)> _slots = [];
    TaskbarRowKind? _pressed;
    Point _pressPoint, _grab;
    bool _dragging;
    TaskbarLayout? _beforeDrag;
    Border? _ghost;

    /// <param name="apply">布局变化时调用（实时预览）；关闭时再调用一次，传入最终结果（确定）或原设置（取消）</param>
    /// <param name="displayed">任务栏数据当前是否开着，关着时提示一下预览只在这个窗口里</param>
    public TaskbarEditor(TaskbarLayout layout, SensorSnapshot? snapshot, bool displayed, Action<TaskbarLayout> apply)
    {
        InitializeComponent();
        _original = layout.Clone();
        _layout = layout.Clone();
        _snapshot = snapshot;
        _apply = apply;
        _selected = _layout.Columns.SelectMany(c => c).FirstOrDefault()?.Kind ?? TaskbarRowKind.Cpu;
        HintText.Text = !TaskbarPanel.Supported ? "任务栏数据需要 Windows 11。"
            : displayed ? "修改会立即显示在任务栏上；任务栏空间不够时会从最右边的一列开始省略。"
            : "任务栏数据当前未开启，可在“查看 → 任务栏数据”里选择显示位置。";
        SyncAll();
        Loaded += (_, _) => RenderPreview();
        DpiChanged += (_, _) => RenderPreview();
    }

    /// <summary>传感器每次刷新时调用，预览和当前值跟着变。</summary>
    public void UpdateSnapshot(SensorSnapshot s)
    {
        _snapshot = s;
        if (_selected == TaskbarRowKind.Fan && FanIds() != _fanIds)
            BuildDetail(); // 风扇列表变了（一般是刚出数）
        else
            UpdateDetailValues();
        RenderPreview();
    }

    void SyncAll()
    {
        _syncing = true;
        MetersBox.IsChecked = _layout.Meters;
        (_layout.NetLabel switch { NetLabelStyle.Text => NetTextRadio, NetLabelStyle.Both => NetBothRadio, _ => NetArrowRadio }).IsChecked = true;
        _syncing = false;
        BuildLayout();
        BuildDetail();
    }

    /// <summary>布局改了：应用到任务栏，刷新预览。</summary>
    void Changed()
    {
        _apply(_layout);
        RenderPreview();
    }

    IEnumerable<TaskbarRow> AllRows => _layout.Columns.SelectMany(c => c).Concat(_layout.Hidden);

    TaskbarRow RowOf(TaskbarRowKind kind) => AllRows.First(r => r.Kind == kind);

    // ───────────────────────────── 布局区 ─────────────────────────────

    /// <summary>按布局重建所有卡片和空位。拖动中末尾多一列空位，用来拖出新的一列；拖空的列先保留，松手后再去掉。</summary>
    void BuildLayout()
    {
        _slots.Clear();
        ColumnsPanel.Children.Clear();
        PoolPanel.Children.Clear();

        int count = _layout.Columns.Count + (_dragging ? 1 : 0);
        for (int c = 0; c < count; c++)
        {
            var rows = c < _layout.Columns.Count ? _layout.Columns[c] : [];
            var panel = new StackPanel { Margin = new Thickness(0, 0, ColumnGap, 0) };
            for (int r = 0; r < TaskbarLayout.MaxRows; r++)
            {
                FrameworkElement slot = r < rows.Count ? Chip(rows[r], true) : EmptySlot();
                slot.Margin = new Thickness(0, 0, 0, r < TaskbarLayout.MaxRows - 1 ? SlotGap : 0);
                panel.Children.Add(slot);
                _slots.Add((slot, new Target(c, r)));
            }
            ColumnsPanel.Children.Add(panel);
        }

        foreach (var row in _layout.Hidden)
        {
            var chip = Chip(row, true);
            chip.Margin = new Thickness(0, 0, SlotGap, SlotGap);
            PoolPanel.Children.Add(chip);
        }
        if (_layout.Hidden.Count == 0)
            PoolPanel.Children.Add(new TextBlock { Text = "把项目拖到这里即可隐藏", Opacity = 0.5, Margin = new Thickness(4, 8, 0, 14) });

        // 立即排版，拖动中下一次命中测试要用到新卡片的位置
        LayoutArea.UpdateLayout();
    }

    /// <summary>一个项目卡片：拖动手柄 + 名称 + 显示内容摘要。</summary>
    Border Chip(TaskbarRow row, bool interactive)
    {
        var spec = TaskbarCatalog.Row(row.Kind);
        var grip = new TextBlock { Text = "", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        grip.SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        var name = new TextBlock { Text = spec.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var summary = new TextBlock
        {
            Text = Summary(row), FontSize = 11.5, Opacity = 0.6, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(grip, Dock.Left);
        DockPanel.SetDock(name, Dock.Left);
        content.Children.Add(grip);
        content.Children.Add(name);
        content.Children.Add(summary);

        var chip = new Border
        {
            Width = SlotWidth, Height = SlotHeight, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 8, 0), Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            Cursor = Cursors.SizeAll, Child = content, Tag = row.Kind,
            ToolTip = interactive ? $"{spec.Name}：{Summary(row)}" : null,
        };
        if (row.Kind == _selected || !interactive)
        {
            chip.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            chip.BorderThickness = new Thickness(1.5);
        }
        if (interactive)
        {
            chip.MouseLeftButtonDown += Chip_MouseLeftButtonDown;
            if (_dragging && row.Kind == _pressed)
                chip.Opacity = 0.35; // 拖动中：原地留一个淡的，表示松手后落在这里
        }
        return chip;
    }

    static FrameworkElement EmptySlot() => new Grid
    {
        Width = SlotWidth, Height = SlotHeight,
        Children =
        {
            new Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), StrokeThickness = 1,
                StrokeDashArray = [4, 3], RadiusX = 5, RadiusY = 5,
            },
        },
    };

    /// <summary>卡片上显示的摘要，比如“占用率 · 温度”。</summary>
    static string Summary(TaskbarRow row)
    {
        if (row.Kind == TaskbarRowKind.Fan)
            return row.Fans.Count == 0 ? "自动" : $"{row.Fans.Count} 个风扇";
        var spec = TaskbarCatalog.Row(row.Kind);
        if (spec.Metrics.Length == 1)
            return "";
        var shown = TaskbarCatalog.Shown(row).Select(m => m.Name).ToList();
        return shown.Count == 0 ? "（未选择）" : string.Join(" · ", shown);
    }

    // ───────────────────────────── 拖动 ─────────────────────────────

    void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var chip = (Border)sender;
        var kind = (TaskbarRowKind)chip.Tag;
        _pressPoint = e.GetPosition(LayoutArea);
        _grab = e.GetPosition(chip);
        if (_selected != kind)
        {
            _selected = kind;
            BuildDetail();
            BuildLayout(); // 更新选中高亮（sender 随之被替换，鼠标捕获放在 LayoutArea 上不受影响）
        }
        _pressed = kind;
        LayoutArea.CaptureMouse();
        e.Handled = true;
    }

    void LayoutArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not TaskbarRowKind kind || !LayoutArea.IsMouseCaptured)
            return;
        var p = e.GetPosition(LayoutArea);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            StartDrag(kind);
        }

        Canvas.SetLeft(_ghost, p.X - _grab.X);
        Canvas.SetTop(_ghost, p.Y - _grab.Y);
        AutoScroll(e.GetPosition(ColumnsScroll));

        if (HitTarget(p) is Target t && Move(kind, t))
        {
            BuildLayout();
            Changed();
        }
    }

    void StartDrag(TaskbarRowKind kind)
    {
        _dragging = true;
        _beforeDrag = _layout.Clone();
        _ghost = Chip(RowOf(kind), false);
        _ghost.Opacity = 0.9;
        DragLayer.Children.Add(_ghost);
        BuildLayout();
    }

    /// <summary>拖到列区两端时自动横向滚动（列多时）。</summary>
    void AutoScroll(Point p)
    {
        const double edge = 30;
        if (p.Y < 0 || p.Y > ColumnsScroll.ActualHeight)
            return;
        if (p.X > ColumnsScroll.ActualWidth - edge)
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset + 8);
        else if (p.X < edge)
            ColumnsScroll.ScrollToHorizontalOffset(ColumnsScroll.HorizontalOffset - 8);
    }

    void LayoutArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (LayoutArea.IsMouseCaptured)
            LayoutArea.ReleaseMouseCapture(); // 收尾在 LostMouseCapture 里
    }

    /// <summary>正常松手、按 Esc、切到别的窗口等情况都会丢失捕获，统一在这里收尾。</summary>
    void LayoutArea_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _pressed = null;
        if (!_dragging)
            return;
        _dragging = false;
        DragLayer.Children.Clear();
        _ghost = null;
        _layout.Normalize(); // 去掉拖空的列
        BuildLayout();
        BuildDetail(); // 标题里的“当前未显示”可能变了
        Changed();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 拖动中按 Esc：撤销这次拖动（而不是关闭窗口）
        if (e.Key == Key.Escape && _dragging && _beforeDrag != null)
        {
            _layout = _beforeDrag;
            LayoutArea.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>鼠标下面是哪个位置：“未显示”区，或者离鼠标最近的格子。</summary>
    Target? HitTarget(Point p)
    {
        var pool = Bounds(PoolArea);
        if (p.Y >= pool.Top - SlotGap)
            return new Target(-1, 0);
        Target? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (slot, target) in _slots)
        {
            var r = Bounds(slot);
            double dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
            double d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = target;
            }
        }
        return best;
    }

    Rect Bounds(FrameworkElement el) =>
        el.TransformToAncestor(LayoutArea).TransformBounds(new Rect(el.RenderSize));

    /// <summary>
    /// 把一行挪到目标位置：目标是空位就放进去；目标上已经有别的行就两者互换（从“未显示”拖来时，被换下的行进“未显示”）。
    /// 返回布局是否有变化。
    /// </summary>
    bool Move(TaskbarRowKind kind, Target t)
    {
        var from = _layout.Columns.FirstOrDefault(c => c.Any(r => r.Kind == kind)) ?? _layout.Hidden;
        int index = from.FindIndex(r => r.Kind == kind);
        var row = from[index];

        if (t.Col < 0)
        {
            if (from == _layout.Hidden)
                return false;
            from.RemoveAt(index);
            _layout.Hidden.Add(row);
            return true;
        }

        if (t.Col == _layout.Columns.Count)
            _layout.Columns.Add([]);
        var to = _layout.Columns[t.Col];
        if (t.Row < to.Count)
        {
            var other = to[t.Row];
            if (other == row)
                return false;
            to[t.Row] = row;
            from[index] = other;
            return true;
        }
        if (to == from)
            return false; // 自己这一列下面的空位：位置不变
        from.RemoveAt(index);
        to.Add(row);
        return true;
    }

    // ───────────────────────────── 显示内容 ─────────────────────────────

    string FanIds() => _snapshot == null ? "" : string.Join("|", SensorPicks.Fans(_snapshot).Select(f => f.Id));

    /// <summary>下方“显示内容”：选中行可选的数据项（风扇行列出检测到的所有风扇）。</summary>
    void BuildDetail()
    {
        var row = RowOf(_selected);
        var spec = TaskbarCatalog.Row(row.Kind);
        bool hidden = _layout.Hidden.Contains(row);
        DetailTitle.Text = $"“{spec.Name}”显示内容" + (hidden ? "（当前未显示，拖到上面的列里才会显示）" : "");
        DetailPanel.Children.Clear();
        _detailBoxes.Clear();
        string? note = null;

        if (row.Kind == TaskbarRowKind.Fan)
        {
            _fanIds = FanIds();
            var fans = _snapshot == null ? [] : SensorPicks.Fans(_snapshot).ToList();
            foreach (var fan in fans)
            {
                string id = fan.Id;
                AddBox(FanName(fan), row.Fans.Contains(id),
                    s => s.ById.GetValueOrDefault(id) is { } r ? SensorFormat.Format(r.Type, r.Value) : null,
                    on => { row.Fans.Remove(id); if (on) row.Fans.Add(id); }, 300);
            }
            note = _snapshot == null ? "正在等待传感器数据…"
                : fans.Count == 0 ? "没有检测到风扇转速传感器（主板风扇需要管理员权限和 PawnIO 驱动）。"
                : "按勾选顺序显示；一个都不勾选时自动显示一个正在转的风扇（优先主板风扇）。";
        }
        else if (spec.Metrics.Length == 1)
        {
            note = $"这一行只有一项数据：{spec.Metrics[0].Name}。"
                + (row.Kind is TaskbarRowKind.Up or TaskbarRowKind.Down ? "标签显示箭头还是文字，在下方“上传/下载标签”里选择。" : "");
        }
        else
        {
            foreach (var m in spec.Metrics)
            {
                var metric = m;
                AddBox(m.Name, row.Metrics.Contains(m.Metric),
                    s => TaskbarCatalog.Cell(metric.Format, metric.Value(s), false) is { } c ? $"{c.Value} {c.Unit}" : null,
                    on => { row.Metrics.Remove(metric.Metric); if (on) row.Metrics.Add(metric.Metric); });
            }
        }
        DetailNote.Text = note;
        DetailNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateDetailValues();
    }

    /// <summary>
    /// 风扇的简短名称：主板风扇的硬件名是“主板型号 › 监控芯片”，太长，统称“主板”；
    /// 显卡风扇的传感器名本身就是“GPU 风扇 1”，不用再加显卡型号。
    /// </summary>
    static string FanName(SensorReading fan) => fan.HardwareType switch
    {
        LibreHardwareMonitor.Hardware.HardwareType.SuperIO or LibreHardwareMonitor.Hardware.HardwareType.Motherboard => $"主板 · {fan.DisplayName}",
        LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd
            or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel => fan.DisplayName,
        _ => $"{fan.HardwareDisplayName} · {fan.DisplayName}",
    };

    /// <param name="width">固定宽度（风扇多时排成整齐的两列），为 0 时按内容</param>
    void AddBox(string label, bool isChecked, Func<SensorSnapshot, string?> value, Action<bool> toggle, double width = 0)
    {
        var box = new CheckBox { IsChecked = isChecked, Margin = new Thickness(0, 0, 18, 0) };
        if (width > 0)
            box.Width = width;
        RoutedEventHandler changed = (_, _) =>
        {
            toggle(box.IsChecked == true);
            BuildLayout(); // 卡片摘要跟着变
            Changed();
        };
        box.Checked += changed;
        box.Unchecked += changed;
        DetailPanel.Children.Add(box);
        _detailBoxes.Add((box, label, value));
    }

    /// <summary>勾选框后面附上当前值，读不到的注明“无数据”，免得勾上了却奇怪为什么不显示。</summary>
    void UpdateDetailValues()
    {
        foreach (var (box, label, value) in _detailBoxes)
            box.Content = _snapshot == null ? label : $"{label}  {value(_snapshot) ?? "（无数据）"}";
    }

    // ───────────────────────────── 其他 ─────────────────────────────

    void NetLabel_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        _layout.NetLabel = Enum.Parse<NetLabelStyle>((string)((FrameworkElement)sender).Tag);
        Changed();
    }

    void Meters_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        _layout.Meters = MetersBox.IsChecked == true;
        Changed();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _layout = TaskbarCatalog.DefaultLayout();
        _selected = TaskbarRowKind.Cpu;
        SyncAll();
        Changed();
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _saved = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _apply(_saved ? _layout.Normalize() : _original);
    }

    void RenderPreview()
    {
        if (!IsLoaded)
            return;
        bool light = TaskbarPanel.SystemUsesLightTheme();
        // 近似 Win11 任务栏的底色
        PreviewHost.Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xF1, 0xF5) : Color.FromRgb(0x1C, 0x1C, 0x1C));

        if (_snapshot == null)
        {
            ShowEmpty("正在等待传感器数据…");
            return;
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        using var bmp = TaskbarPanel.RenderPreview(_snapshot, _layout, (int)Math.Round(dpi), light);
        if (bmp == null)
        {
            ShowEmpty(_layout.Columns.Any(c => c.Count > 0)
                ? "布局里的项目在这台电脑上都读不到数据" : "布局里没有任何项目，任务栏上将不显示数据");
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
