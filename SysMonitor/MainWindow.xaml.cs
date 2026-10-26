using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Win32;
using PawnIoDriver = LibreHardwareMonitor.PawnIo.PawnIo;

namespace SysMonitor;

public partial class MainWindow : Window
{
    const string TagSensors = "sensors";
    const string TagGraphs = "graphs";

    public static readonly DependencyProperty GraphColumnsProperty =
        DependencyProperty.Register(nameof(GraphColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(2));

    /// <summary>曲线页的列数，窗口窄时变成一列。</summary>
    public int GraphColumns
    {
        get => (int)GetValue(GraphColumnsProperty);
        set => SetValue(GraphColumnsProperty, value);
    }

    readonly AppSettings _settings = App.Settings;
    readonly SensorService _sensors = new();
    readonly TrayIcon _tray;
    readonly TaskbarPanel _taskbar;
    TaskbarEditor? _taskbarEditor;
    readonly Dictionary<string, SensorRow> _rowById = [];
    readonly HashSet<string> _everHadValue = [];
    readonly ObservableCollection<GraphModel> _graphs = [];

    List<InfoPage> _pages = [];
    string? _infoError;
    bool _loadingInfo;
    List<SensorRow> _allRows = [];
    readonly Dictionary<string, SensorGroup> _groups = [];
    readonly ObservableCollection<object> _sensorItems = [];
    int _populateGeneration;
    SensorSnapshot? _last;
    int _snapshotCount;
    string _signature = "";
    string? _currentTag;
    bool _bannerNeeded;
    bool _closed;
    bool _exiting, _tipShown;

    public MainWindow()
    {
        InitializeComponent();
        GraphList.ItemsSource = _graphs;
        SensorList.ItemsSource = _sensorItems;
        _tray = new TrayIcon(RestoreFromTray, ExitApp);
        // 任务栏数据的回调在它自己的线程上触发，切回界面线程
        _taskbar = new TaskbarPanel(
            () => Dispatcher.BeginInvoke(RestoreFromTray),
            () => Dispatcher.BeginInvoke(ExitApp),
            side => Dispatcher.BeginInvoke(() => SetTaskbar(side)),
            () => Dispatcher.BeginInvoke(EditTaskbar));
        _taskbar.SetItems(_settings.TaskbarItems);

        _sensors.IntervalMs = _settings.IntervalMs;
        TrayItem.IsChecked = _settings.MinimizeToTray;
        CloseTrayItem.IsChecked = _settings.CloseToTray;
        // 关机、注销时不能拦着不关
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        TaskbarMenu.IsEnabled = TaskbarPanel.Supported;
        SetTaskbar(_settings.TaskbarDisplay ? _settings.TaskbarSide : null);
        SyncMenuChecks();
        RefreshStartupMenu();
        InitDriverStatus();
        GraphCard.Dark = ThemeHelper.IsDark(_settings.Theme);

        _sensors.Updated += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        _sensors.Failed += msg => Dispatcher.BeginInvoke(() => StatsText.Text = "传感器错误：" + msg);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Loaded += (_, _) =>
        {
            ((TreeViewItem)((TreeViewItem)Nav.Items[0]).Items[0]).IsSelected = true;
            LoadInfo();
            _sensors.Start();
        };
    }

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t)
                return t;
            if (FindChild<T>(child) is { } found)
                return found;
        }
        return null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 点 × 只隐藏到托盘，传感器和任务栏数据照常运行；真正退出走托盘菜单或“文件 → 退出”
        if (!_exiting && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_tipShown)
            {
                _tipShown = true;
                _tray.ShowTip("SysMonitor 仍在运行", "已退出到托盘。双击托盘图标重新打开，右键托盘图标 → 退出 可彻底关闭。");
            }
            return;
        }
        base.OnClosing(e);
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        _taskbarEditor?.Close(); // 没有所有者时它不会跟着关，留着会让程序退不掉
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _sensors.Dispose();
        _tray.Dispose();
        _taskbar.Dispose();
    }

    void InitDriverStatus()
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        bool driver = PawnIoDriver.IsInstalled;
        DriverText.Text = driver ? $"PawnIO 驱动 v{PawnIoDriver.Version}" : "PawnIO 驱动未安装";

        if (!admin)
        {
            BannerText.Text = "当前不是管理员权限，温度、电压、风扇等大部分传感器无法读取。请右键程序 →“以管理员身份运行”。";
            _bannerNeeded = true;
        }
        else if (!driver)
        {
            var link = new Hyperlink(new Run("pawnio.eu")) { NavigateUri = new Uri("https://pawnio.eu/") };
            link.RequestNavigate += (_, e) => OpenShell(e.Uri.AbsoluteUri);
            BannerText.Inlines.Add("未检测到 PawnIO 驱动，CPU 温度、主板电压/风扇等数据无法读取。请从 ");
            BannerText.Inlines.Add(link);
            BannerText.Inlines.Add(" 下载安装后重启本程序。");
            _bannerNeeded = true;
        }
    }

    // ───────────────────────────── 页面切换 ─────────────────────────────

    void Nav_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item)
            ShowView(item.Tag as string);
    }

    void ShowView(string? tag)
    {
        _currentTag = tag;
        UIElement view;
        if (tag == TagSensors)
        {
            if (_last == null)
            {
                Placeholder.Text = "正在初始化传感器，首次启动可能需要十几秒…";
                view = Placeholder;
            }
            else
            {
                view = SensorView;
            }
        }
        else if (tag == TagGraphs)
        {
            view = GraphView;
        }
        else
        {
            var page = _pages.FirstOrDefault(p => p.Title == tag);
            if (page != null)
            {
                FillInfo(page);
                view = InfoList;
            }
            else
            {
                Placeholder.Text = _infoError ?? "正在收集硬件信息…";
                view = Placeholder;
            }
        }

        foreach (var v in new UIElement[] { InfoList, SensorView, GraphView, Placeholder })
            v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
        // 传感器页不显示时用 Hidden 而不是 Collapsed：仍参与布局，列表行能在后台分批建好，切过去时不卡
        if (view != SensorView)
            SensorView.Visibility = Visibility.Hidden;
        Banner.Visibility = _bannerNeeded && tag == TagSensors ? Visibility.Visible : Visibility.Collapsed;
    }

    async void LoadInfo()
    {
        if (_loadingInfo)
            return;
        _loadingInfo = true;
        _infoError = null;
        try
        {
            _pages = await Task.Run(SystemInfo.CollectAll);
        }
        catch (Exception ex)
        {
            _infoError = "收集硬件信息失败：" + ex.Message;
        }
        finally
        {
            _loadingInfo = false;
        }
        ShowView(_currentTag);
    }

    void FillInfo(InfoPage page)
    {
        var rows = page.Groups.SelectMany(g => g.Items.Select(i => new InfoRow(g.Title, i.Name, i.Value))).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InfoRow.Group)));
        InfoList.ItemsSource = view;
        InfoList.ScrollIntoView(rows.FirstOrDefault());
    }

    void InfoList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // GridView 的列不能自动铺满，手动让“值”列占满剩余宽度
        InfoValueColumn.Width = Math.Max(200, InfoList.ActualWidth - InfoNameColumn.ActualWidth - 40);
    }

    // ───────────────────────────── 传感器 ─────────────────────────────

    void OnSnapshot(SensorSnapshot s)
    {
        // 窗口关闭后，排队中的最后一次刷新可能还会到达，此时托盘图标已释放
        if (_closed)
            return;
        _last = s;
        _snapshotCount++;

        bool newlyHasValue = false;
        foreach (var r in s.Readings)
            if (r.Value.HasValue && _everHadValue.Add(r.Id))
                newlyHasValue = true;

        // 传感器集合变化时整体重建（一般只在启动时发生一次）
        var signature = string.Join("\n", s.Readings.Select(r => r.Id));
        if (signature != _signature)
        {
            _signature = signature;
            _rowById.Clear();
            _allRows = [];
            foreach (var r in s.Readings)
            {
                var row = new SensorRow(r) { InGraph = _graphs.Any(g => g.Id == r.Id) };
                _rowById[r.Id] = row;
                _allRows.Add(row);
            }
            BuildSensorItems();
        }
        else if (newlyHasValue && ShowEmptyBox.IsChecked != true)
        {
            ApplySensorFilter(); // 有传感器第一次出值，从隐藏变为显示
        }

        // 只改变化了的属性，绑定系统只刷新对应的格子
        foreach (var r in s.Readings)
            if (_rowById.TryGetValue(r.Id, out var row))
                row.Update(r);

        // 第一次刷新时负载类传感器还没值，第二次再挑默认曲线
        if (_snapshotCount == 2)
        {
            foreach (var r in new[] { SensorPicks.CpuLoad(s), SensorPicks.CpuTemp(s), SensorPicks.MemLoad(s), SensorPicks.GpuLoad(s), SensorPicks.GpuTemp(s) })
                if (r != null)
                    AddGraph(r);
        }
        foreach (var g in _graphs)
            g.Push(s.ById.TryGetValue(g.Id, out var r) ? r.Value : null);

        if (_currentTag == TagSensors && SensorView.Visibility != Visibility.Visible)
            ShowView(TagSensors);

        var stats = SensorPicks.QuickStats(s);
        StatsText.Text = string.Join("      ", stats.Append($"传感器 {s.Readings.Count} 个").Append($"更新于 {s.Time:HH:mm:ss}"));
        _tray.Update(s, stats);
        _taskbar.Update(s);
        _taskbarEditor?.UpdateValues(BarValues.From(s));
    }

    bool SensorVisible(SensorRow row)
    {
        if (ShowEmptyBox.IsChecked != true && !_everHadValue.Contains(row.Id))
            return false;
        var f = FilterBox.Text;
        if (string.IsNullOrEmpty(f))
            return true;
        return row.SearchText.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    // 打字停顿一下再过滤，避免每个字符都重建一次列表
    System.Windows.Threading.DispatcherTimer? _filterTimer;

    void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_filterTimer == null)
        {
            _filterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _filterTimer.Tick += (_, _) =>
            {
                _filterTimer.Stop();
                ApplySensorFilter();
            };
        }
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    void ShowEmpty_Changed(object sender, RoutedEventArgs e) => ApplySensorFilter();

    /// <summary>传感器集合变化时（一般只在启动时一次）生成“分组标题 + 行”的扁平列表，并分批填入界面。</summary>
    void BuildSensorItems()
    {
        foreach (var g in _groups.Values)
            g.Rows.Clear();
        var items = new List<object>(_allRows.Count + 32);
        SensorGroup? group = null;
        foreach (var row in _allRows)
        {
            if (group?.Id != row.HardwareId)
            {
                group = GetGroup(row);
                items.Add(group);
            }
            group.Rows.Add(row);
            row.Group = group;
            items.Add(row);
        }
        ApplySensorFilter();
        PopulateSensorItems(items);
    }

    /// <summary>
    /// 分批把列表项加进界面：每批 30 项，批与批之间让出 UI 线程，避免一次性创建三百多行卡住界面。
    /// 传感器页不显示时是 Hidden（仍参与布局），所以行会在后台提前建好，切过去时已就绪。
    /// </summary>
    async void PopulateSensorItems(List<object> items)
    {
        int generation = ++_populateGeneration;
        _sensorItems.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            _sensorItems.Add(items[i]);
            if (i % 30 == 29)
            {
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                if (generation != _populateGeneration)
                    return; // 期间又重建了
            }
        }
    }

    /// <summary>
    /// 按搜索词 / “显示无数据项”更新每行是否显示（只改行高，不重建）。
    /// 分组展开状态：平时按用户上次保存的状态（没保存过默认折叠）；搜索时命中的分组临时展开，不改保存值。
    /// </summary>
    void ApplySensorFilter()
    {
        bool filtering = !string.IsNullOrEmpty(FilterBox.Text);
        foreach (var row in _allRows)
            row.Matches = SensorVisible(row);
        foreach (var g in _groups.Values)
        {
            g.Count = g.Rows.Count(r => r.Matches);
            g.SetExpandedInstantly(filtering || SavedExpanded(g.Id));
        }
    }

    SensorGroup GetGroup(SensorRow row)
    {
        if (!_groups.TryGetValue(row.HardwareId, out var group))
        {
            group = new SensorGroup(row.HardwareId, row.Hardware, SavedExpanded(row.HardwareId));
            group.UserToggled += g =>
            {
                g.VisibleRowBudget = VisibleRowBudget(g);
                _settings.SensorGroups[g.Id] = g.IsExpanded;
                _settings.Save();
            };
            _groups[row.HardwareId] = group;
        }
        return group;
    }

    /// <summary>根据分组标题在可视区域里的位置，估算屏幕上最多能看到本组几行（多算 2 行余量）。</summary>
    int VisibleRowBudget(SensorGroup group)
    {
        if (SensorList.ItemContainerGenerator.ContainerFromItem(group) is not FrameworkElement header
            || FindChild<ScrollViewer>(SensorList) is not { } viewer)
            return int.MaxValue;
        double headerBottom = header.TranslatePoint(new Point(0, header.ActualHeight), viewer).Y;
        return Math.Max(0, (int)Math.Ceiling((viewer.ViewportHeight - headerBottom) / SensorRow.BaseHeight) + 2);
    }

    bool SavedExpanded(string hardwareId) => _settings.SensorGroups.TryGetValue(hardwareId, out var expanded) && expanded;

    // ───────────────────────────── 曲线 ─────────────────────────────

    void AddGraph(SensorReading r)
    {
        if (_graphs.Any(g => g.Id == r.Id))
            return;
        int color = _graphs.Count == 0 ? 0 : _graphs.Max(g => g.ColorIndex) + 1;
        _graphs.Add(new GraphModel(r.Id, $"{r.HardwareDisplayName} · {r.DisplayName}", r.Type, color));
        if (_rowById.TryGetValue(r.Id, out var row))
            row.InGraph = true;
    }

    void RemoveGraph(GraphModel g)
    {
        _graphs.Remove(g);
        if (_rowById.TryGetValue(g.Id, out var row))
            row.InGraph = false;
    }

    void ToggleGraph(SensorRow row)
    {
        var existing = _graphs.FirstOrDefault(g => g.Id == row.Id);
        if (existing != null)
            RemoveGraph(existing);
        else if (_last != null && _last.ById.TryGetValue(row.Id, out var r))
            AddGraph(r);
    }

    void SensorList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 只响应双击行本身，避免双击分组标题或滚动条时误触发
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(SensorList, d) is ListBoxItem { DataContext: SensorRow row })
            ToggleGraph(row);
    }

    void ToggleGraph_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SensorList.SelectedItems.OfType<SensorRow>().ToList())
            ToggleGraph(row);
    }

    void RemoveGraph_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GraphModel g)
            RemoveGraph(g);
    }

    void GraphView_SizeChanged(object sender, SizeChangedEventArgs e) =>
        GraphColumns = GraphView.ActualWidth >= 900 ? 2 : 1;

    // ───────────────────────────── 菜单 ─────────────────────────────

    void SyncMenuChecks()
    {
        foreach (MenuItem mi in IntervalMenu.Items)
            mi.IsChecked = int.Parse((string)mi.Tag) == _settings.IntervalMs;
        foreach (MenuItem mi in ThemeMenu.Items)
            mi.IsChecked = (string)mi.Tag == _settings.Theme.ToString();
    }

    void Interval_Click(object sender, RoutedEventArgs e)
    {
        int ms = int.Parse((string)((MenuItem)sender).Tag);
        _sensors.IntervalMs = ms;
        _settings.IntervalMs = ms;
        _settings.Save();
        SyncMenuChecks();
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = Enum.Parse<ThemeChoice>((string)((MenuItem)sender).Tag);
        _settings.Save();
        SyncMenuChecks();
        // Fluent 主题：所有标准控件、标题栏一起切换；“跟随系统”时会自动跟着系统变
        Application.Current.ThemeMode = _settings.Theme.ToWpf();
        RefreshGraphTheme();
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme == ThemeChoice.System && e.Category == UserPreferenceCategory.General)
            Dispatcher.BeginInvoke(RefreshGraphTheme);
    }

    /// <summary>曲线是自绘的，不走 Fluent 资源，主题变化时单独通知重绘。</summary>
    void RefreshGraphTheme()
    {
        GraphCard.Dark = ThemeHelper.IsDark(_settings.Theme);
        foreach (var g in _graphs)
            g.Touch();
    }

    void ResetMinMax_Click(object sender, RoutedEventArgs e) => _sensors.ResetMinMax();

    void CloseTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = CloseTrayItem.IsChecked;
        _settings.Save();
    }

    void Tray_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinimizeToTray = TrayItem.IsChecked;
        _settings.Save();
    }

    void Taskbar_Click(object sender, RoutedEventArgs e) =>
        SetTaskbar(Enum.TryParse<TaskbarSide>((string)((MenuItem)sender).Tag, out var side) ? side : null);

    /// <summary>任务栏数据显示在哪一侧，null 为不显示。</summary>
    void SetTaskbar(TaskbarSide? side)
    {
        bool on = side != null;
        if (side is TaskbarSide s)
            _settings.TaskbarSide = s;
        _settings.TaskbarDisplay = on;
        _settings.Save();
        foreach (var mi in TaskbarMenu.Items.OfType<MenuItem>().Where(mi => mi.Tag is string))
            mi.IsChecked = (string)mi.Tag == (on ? _settings.TaskbarSide.ToString() : "Off");
        _taskbar.Configure(on, _settings.TaskbarSide);
    }

    void TaskbarEdit_Click(object sender, RoutedEventArgs e) => EditTaskbar();

    /// <summary>打开（或激活已打开的）任务栏显示内容编辑窗口。</summary>
    void EditTaskbar()
    {
        if (_taskbarEditor != null)
        {
            _taskbarEditor.Activate();
            return;
        }
        _taskbarEditor = new TaskbarEditor(_settings.TaskbarItems, _last is { } s ? BarValues.From(s) : null, _settings.TaskbarDisplay,
            items =>
            {
                _settings.TaskbarItems = items;
                _taskbar.SetItems(items);
            });
        _taskbarEditor.Closed += (_, _) =>
        {
            _taskbarEditor = null;
            _settings.Save();
        };
        // 主窗口藏在托盘里时（从任务栏右键菜单打开）不设所有者，单独居中显示
        if (IsVisible)
            _taskbarEditor.Owner = this;
        else
            _taskbarEditor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _taskbarEditor.Show();
        _taskbarEditor.Activate();
    }

    void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostItem.IsChecked;

    void RefreshInfo_Click(object sender, RoutedEventArgs e) => LoadInfo();

    void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();

    void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "SysMonitor 系统监控 2.0（WPF）\n\n" +
        "传感器：LibreHardwareMonitorLib（MPL-2.0）\n" +
        "硬件访问驱动：PawnIO（GPL-2.0）\n" +
        "硬件信息：WMI / Windows API",
        "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    void Export_Click(object sender, RoutedEventArgs e) => ExportReport();

    void ExportReport()
    {
        if (_pages.Count == 0)
        {
            MessageBox.Show(this, "硬件信息还在收集中，请稍后再试。", Title);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"SysMonitor_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, Report.Build(_pages, _last), new UTF8Encoding(true));
            if (MessageBox.Show(this, "报告已保存，是否立即打开？", Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                OpenShell(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存失败：" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────────────────────── 其他 ─────────────────────────────

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            LoadInfo();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ExportReport();
            e.Handled = true;
        }
    }

    void ListCopy_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not ListBox list)
            return;
        var lines = list.SelectedItems.Cast<object>().Select(o => o switch
        {
            InfoRow r => $"{r.Name}\t{r.Value}",
            SensorRow s => $"{s.Hardware}\t{s.Name}\t{s.Value}\t{s.Min}\t{s.Max}",
            _ => "",
        });
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0)
            Clipboard.SetText(text);
    }

    void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
            Hide();
    }

    void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    static void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }
}
