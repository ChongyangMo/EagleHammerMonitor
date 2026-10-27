using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace EagleHammerMonitor;

/// <summary>硬件信息页的一行，按 Group 分组显示。</summary>
public sealed record InfoRow(string Group, string Name, string Value);

/// <summary>传感器表格的一行。只有值真的变了才发通知，WPF 只重绘对应的格子。</summary>
public sealed class SensorRow(SensorReading r) : INotifyPropertyChanged
{
    string _value = "", _min = "", _max = "";
    int _level;
    bool _inGraph;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; } = r.Id;
    public string Hardware { get; } = r.HardwareDisplayName;
    public string Name { get; } = r.DisplayName;

    /// <summary>英文原名，鼠标悬停时显示，搜索时也会匹配。</summary>
    public string OriginalName { get; } = r.HardwareName == r.HardwareDisplayName ? r.Name : $"{r.Name}（{r.HardwareName}）";

    /// <summary>搜索用：中英文名称、硬件名、类型都能搜到。</summary>
    public string SearchText { get; } = string.Join(" | ", r.DisplayName, r.Name, r.HardwareDisplayName, r.HardwareName, SensorFormat.TypeName(r.Type));

    public string TypeName { get; } = SensorFormat.TypeName(r.Type);

    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Min { get => _min; private set => Set(ref _min, value); }
    public string Max { get => _max; private set => Set(ref _max, value); }

    /// <summary>0 正常，1 偏热（≥70°C），2 过热（≥85°C）。</summary>
    public int Level { get => _level; private set => Set(ref _level, value); }

    public bool InGraph { get => _inGraph; set => Set(ref _inGraph, value); }

    public string HardwareId { get; } = r.HardwareId;

    // ── 折叠动画：行高 = 基准高度 × 所属分组的展开进度 ──

    public const double BaseHeight = 30;

    SensorGroup? _group;

    public SensorGroup? Group
    {
        get => _group;
        set
        {
            _group = value;
            NotifyFold();
        }
    }

    bool _matches = true;

    /// <summary>是否符合当前搜索 / “显示无数据项”条件。</summary>
    public bool Matches
    {
        get => _matches;
        set
        {
            if (_matches == value)
                return;
            _matches = value;
            Raise(nameof(RowHeight));
        }
    }

    /// <summary>是否参与本次折叠动画（只有屏幕上看得到的行参与，其余直接到最终状态）。</summary>
    internal bool Animating { get; set; }

    double Fold => _group == null ? 1 : Animating ? _group.Progress : _group.TargetFold;

    // 隐藏（折叠或不匹配搜索）时只把高度设为 0，不用 Visibility.Collapsed：
    // Collapsed 的行从没参与过布局，模板还没实例化，再显示时要临时创建（约 1ms/行），动画会掉帧
    public double RowHeight => _matches ? BaseHeight * Fold : 0;
    public double RowOpacity => Fold;

    internal void NotifyFold()
    {
        Raise(nameof(RowHeight));
        Raise(nameof(RowOpacity));
    }

    public void Update(SensorReading reading)
    {
        Value = SensorFormat.Format(reading.Type, reading.Value);
        Min = SensorFormat.Format(reading.Type, reading.Min);
        Max = SensorFormat.Format(reading.Type, reading.Max);
        Level = reading.Type == SensorType.Temperature && reading.Value is float t ? (t >= 85 ? 2 : t >= 70 ? 1 : 0) : 0;
    }

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Raise(name);
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 传感器列表里的分组标题（一个硬件一组）。
/// 列表是“标题 + 行”的扁平虚拟化列表；折叠时由这里驱动动画，把组内各行的高度从 30 平滑缩到 0。
/// 只有屏幕上可见的行有绑定，所以每帧开销与组的大小无关。
/// </summary>
public sealed class SensorGroup : INotifyPropertyChanged
{
    const double DurationMs = 220;
    static readonly List<SensorGroup> ActiveGroups = [];
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    bool _expanded;
    double _progress, _from, _target, _start;
    int _count;

    public SensorGroup(string id, string name, bool expanded)
    {
        Id = id;
        Name = name;
        _expanded = expanded;
        _progress = _target = expanded ? 1 : 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 用户点击标题切换展开状态时、动画开始前触发：界面在这里设置 <see cref="VisibleRowBudget"/> 并保存状态。
    /// 程序内部的 <see cref="SetExpandedInstantly"/> 不触发。
    /// </summary>
    public event Action<SensorGroup>? UserToggled;

    /// <summary>本次动画中屏幕上最多能看到本组的几行；只有这些行做动画，其余直接到最终高度。</summary>
    public int VisibleRowBudget { get; set; } = int.MaxValue;

    internal double TargetFold => _target;

    public string Id { get; }
    public string Name { get; }

    /// <summary>这个硬件的全部传感器行（包括当前不匹配搜索的）。</summary>
    public List<SensorRow> Rows { get; } = [];

    /// <summary>当前符合条件的行数，显示在标题上；为 0 时标题隐藏。</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
                return;
            _count = value;
            Raise(nameof(Count));
            Raise(nameof(HeaderVisibility));
        }
    }

    public Visibility HeaderVisibility => _count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>展开状态（与标题上的切换按钮双向绑定），变化时播放动画。</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
                return;
            _expanded = value;
            Raise(nameof(IsExpanded));
            UserToggled?.Invoke(this);
            Animate(value ? 1 : 0);
        }
    }

    /// <summary>0 = 完全折叠，1 = 完全展开。</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            Raise(nameof(ChevronAngle));
            foreach (var row in Rows)
                if (row.Animating)
                    row.NotifyFold();
        }
    }

    /// <summary>箭头角度与折叠进度同步：展开 180°（朝上），折叠 0°（朝下），与 Fluent Expander 一致。</summary>
    public double ChevronAngle => 180 * _progress;

    /// <summary>不播动画直接到位（比如搜索时自动展开）。</summary>
    public void SetExpandedInstantly(bool expanded)
    {
        if (_expanded == expanded && !ActiveGroups.Contains(this))
            return;
        ActiveGroups.Remove(this);
        _expanded = expanded;
        Raise(nameof(IsExpanded));
        _target = expanded ? 1 : 0;
        foreach (var row in Rows)
            row.Animating = false;
        Progress = _target;
        foreach (var row in Rows)
            row.NotifyFold();
    }

    void Animate(double target)
    {
        // 从当前进度继续，动画中途再点也不会跳
        _from = _progress;
        _target = target;
        _start = Clock.Elapsed.TotalMilliseconds;

        // 只让屏幕上看得到的前几行做动画；屏幕外的行直接到最终高度（看不见，效果一样，每帧少算很多行）
        int shown = 0;
        foreach (var row in Rows)
        {
            bool animate = row.Matches && shown < VisibleRowBudget;
            if (row.Matches)
                shown++;
            if (row.Animating != animate)
            {
                row.Animating = animate;
                row.NotifyFold();
            }
        }

        if (ActiveGroups.Contains(this))
            return;
        ActiveGroups.Add(this);
        if (ActiveGroups.Count == 1)
            CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>每渲染一帧推进一次，和屏幕刷新同步。</summary>
    static void OnRendering(object? sender, EventArgs e)
    {
        double now = Clock.Elapsed.TotalMilliseconds;
        for (int i = ActiveGroups.Count - 1; i >= 0; i--)
        {
            var g = ActiveGroups[i];
            double t = Math.Clamp((now - g._start) / DurationMs, 0, 1);
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; // 三次缓入缓出
            g.Progress = g._from + (g._target - g._from) * eased;
            if (t >= 1)
                ActiveGroups.RemoveAt(i);
        }
        if (ActiveGroups.Count == 0)
            CompositionTarget.Rendering -= OnRendering;
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>扁平列表里分组标题和传感器行用不同的容器样式。</summary>
public sealed class SensorItemStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? RowStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) => item is SensorGroup ? HeaderStyle : RowStyle;
}

/// <summary>一条实时曲线的数据（环形缓冲区）。</summary>
public sealed class GraphModel(string id, string title, SensorType type, int colorIndex)
{
    public const int Capacity = 150;
    readonly float?[] _buf = new float?[Capacity];
    int _head;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public SensorType Type { get; } = type;
    public int ColorIndex { get; } = colorIndex;
    public int Count { get; private set; }

    /// <summary>数据或主题变化，需要重绘。</summary>
    public event Action? Changed;

    public void Push(float? v)
    {
        _buf[_head] = v;
        _head = (_head + 1) % Capacity;
        if (Count < Capacity)
            Count++;
        Changed?.Invoke();
    }

    public float? At(int i) => _buf[(_head - Count + i + Capacity) % Capacity];

    public void Touch() => Changed?.Invoke();
}
