namespace SysMonitor;

/// <summary>数值的显示方式：决定数字格式、单位、固定列宽用的模板，以及是否有警示色和占用率小条。</summary>
enum MetricFormat { Percent, Temp, GHz, MHz, Watt, GB, Rate, Rpm }

/// <summary>一项可以显示的数据。</summary>
sealed record MetricSpec(TaskbarMetric Metric, string Name, MetricFormat Format, Func<SensorSnapshot, float?> Value);

/// <summary>任务栏上的一行：标签 + 可选的若干项数据（按这里的顺序排列）。</summary>
sealed record RowSpec(TaskbarRowKind Kind, string Name, string Label, LabelTone Tone, MetricSpec[] Metrics, TaskbarMetric[] Defaults);

/// <summary>任务栏可以显示的全部数据，以及怎样取值、怎样显示。</summary>
static class TaskbarCatalog
{
    public static readonly RowSpec[] Rows =
    [
        new(TaskbarRowKind.Cpu, "CPU", "CPU", LabelTone.Normal,
        [
            new(TaskbarMetric.CpuLoad, "占用率", MetricFormat.Percent, s => SensorPicks.CpuLoad(s)?.Value),
            new(TaskbarMetric.CpuTemp, "温度", MetricFormat.Temp, s => SensorPicks.CpuTemp(s)?.Value),
            new(TaskbarMetric.CpuClock, "频率", MetricFormat.GHz, s => SensorPicks.CpuClock(s)?.Value),
            new(TaskbarMetric.CpuPower, "功耗", MetricFormat.Watt, s => SensorPicks.CpuPower(s)?.Value),
        ], [TaskbarMetric.CpuLoad, TaskbarMetric.CpuTemp]),
        new(TaskbarRowKind.Mem, "内存", "内存", LabelTone.Normal,
        [
            new(TaskbarMetric.MemLoad, "占用率", MetricFormat.Percent, s => SensorPicks.MemLoad(s)?.Value),
            new(TaskbarMetric.MemUsed, "已用", MetricFormat.GB, s => SensorPicks.MemUsed(s)?.Value),
        ], [TaskbarMetric.MemLoad]),
        new(TaskbarRowKind.Gpu, "GPU", "GPU", LabelTone.Normal,
        [
            new(TaskbarMetric.GpuLoad, "占用率", MetricFormat.Percent, s => SensorPicks.GpuLoad(s)?.Value),
            new(TaskbarMetric.GpuTemp, "温度", MetricFormat.Temp, s => SensorPicks.GpuTemp(s)?.Value),
            new(TaskbarMetric.GpuClock, "频率", MetricFormat.MHz, s => SensorPicks.GpuClock(s)?.Value),
            new(TaskbarMetric.GpuPower, "功耗", MetricFormat.Watt, s => SensorPicks.GpuPower(s)?.Value),
            new(TaskbarMetric.GpuFan, "风扇", MetricFormat.Rpm, s => SensorPicks.GpuFan(s)?.Value),
        ], [TaskbarMetric.GpuLoad, TaskbarMetric.GpuTemp]),
        new(TaskbarRowKind.Vram, "显存", "显存", LabelTone.Normal,
        [
            new(TaskbarMetric.VramLoad, "占用率", MetricFormat.Percent, s => SensorPicks.GpuMemLoad(s)?.Value),
            new(TaskbarMetric.VramUsed, "已用", MetricFormat.GB, s => SensorPicks.GpuMemUsed(s)?.Value / 1024),
        ], [TaskbarMetric.VramLoad]),
        new(TaskbarRowKind.Up, "上传", "↑", LabelTone.Up,
        [
            new(TaskbarMetric.NetUp, "上传速度", MetricFormat.Rate, s => SensorPicks.NetRates(s)?.Up),
        ], [TaskbarMetric.NetUp]),
        new(TaskbarRowKind.Down, "下载", "↓", LabelTone.Down,
        [
            new(TaskbarMetric.NetDown, "下载速度", MetricFormat.Rate, s => SensorPicks.NetRates(s)?.Down),
        ], [TaskbarMetric.NetDown]),
        new(TaskbarRowKind.Disk, "磁盘", "磁盘", LabelTone.Normal,
        [
            new(TaskbarMetric.DiskActivity, "活动率", MetricFormat.Percent, SensorPicks.DiskActivity),
            new(TaskbarMetric.DiskTemp, "温度", MetricFormat.Temp, SensorPicks.DiskTemp),
            new(TaskbarMetric.DiskRead, "读取速度", MetricFormat.Rate, s => SensorPicks.DiskRates(s)?.Read),
            new(TaskbarMetric.DiskWrite, "写入速度", MetricFormat.Rate, s => SensorPicks.DiskRates(s)?.Write),
        ], [TaskbarMetric.DiskActivity]),
        // 风扇行不用固定的数据项，显示用户勾选的风扇（TaskbarRow.Fans），没勾选时自动挑一个正在转的
        new(TaskbarRowKind.Fan, "风扇", "风扇", LabelTone.Normal, [], []),
    ];

    public static RowSpec Row(TaskbarRowKind kind) => Rows.First(r => r.Kind == kind);

    /// <summary>默认布局：CPU+内存、GPU+显存、上传+下载三列，磁盘和风扇先不显示。</summary>
    public static TaskbarLayout DefaultLayout() => new TaskbarLayout
    {
        Columns =
        [
            [NewRow(TaskbarRowKind.Cpu), NewRow(TaskbarRowKind.Mem)],
            [NewRow(TaskbarRowKind.Gpu), NewRow(TaskbarRowKind.Vram)],
            [NewRow(TaskbarRowKind.Up), NewRow(TaskbarRowKind.Down)],
        ],
    }.Normalize();

    public static TaskbarRow NewRow(TaskbarRowKind kind) => new() { Kind = kind, Metrics = [.. Row(kind).Defaults] };

    /// <summary>这一行实际显示的数据项（按目录顺序，只有一项可选的行总是显示那一项）。</summary>
    public static IEnumerable<MetricSpec> Shown(TaskbarRow row)
    {
        var spec = Row(row.Kind);
        return spec.Metrics.Length == 1 ? spec.Metrics : spec.Metrics.Where(m => row.Metrics.Contains(m.Metric));
    }

    /// <summary>一行在任务栏上的样子；一项数据都没有时返回 null（这一行不显示）。</summary>
    public static BarRow? BuildRow(SensorSnapshot s, TaskbarRow row, TaskbarLayout layout)
    {
        var spec = Row(row.Kind);
        IEnumerable<BarCell?> cells = row.Kind == TaskbarRowKind.Fan
            ? FanReadings(s, row).Select(r => Cell(MetricFormat.Rpm, r.Value, layout.Meters))
            : Shown(row).Select(m => Cell(m.Format, m.Value(s), layout.Meters));
        var present = cells.OfType<BarCell>().ToArray();
        return present.Length > 0 ? new BarRow(Label(spec, layout.NetLabel), present, spec.Tone) : null;
    }

    /// <summary>行标签；上传/下载按设置显示箭头、文字或两者。</summary>
    static string Label(RowSpec spec, NetLabelStyle net) =>
        spec.Kind is not (TaskbarRowKind.Up or TaskbarRowKind.Down) ? spec.Label
        : net switch
        {
            NetLabelStyle.Text => spec.Name,
            NetLabelStyle.Both => $"{spec.Label} {spec.Name}",
            _ => spec.Label,
        };

    /// <summary>风扇行要显示的风扇：勾选的（按勾选顺序），没勾选时自动挑一个。</summary>
    public static IEnumerable<SensorReading> FanReadings(SensorSnapshot s, TaskbarRow row)
    {
        if (row.Fans.Count == 0)
            return SensorPicks.DefaultFan(s) is { } auto ? [auto] : [];
        return row.Fans.Select(id => s.ById.GetValueOrDefault(id)).OfType<SensorReading>();
    }

    public static BarCell? Cell(MetricFormat format, float? value, bool meters)
    {
        if (value is not float v || float.IsNaN(v) || float.IsInfinity(v))
            return null;
        return format switch
        {
            MetricFormat.Percent => new BarCell($"{v:0}", "%", "100", "%", v >= 90 ? 2 : v >= 75 ? 1 : 0, meters ? Math.Clamp(v / 100, 0, 1) : null),
            MetricFormat.Temp => new BarCell($"{v:0}", "°C", "100", "°C", v >= 85 ? 2 : v >= 70 ? 1 : 0),
            MetricFormat.GHz => new BarCell($"{v / 1000:0.00}", "GHz", "9.99", "GHz"),
            MetricFormat.MHz => new BarCell($"{v:0}", "MHz", "9999", "MHz"),
            MetricFormat.Watt => new BarCell($"{v:0}", "W", "999", "W"),
            MetricFormat.GB => new BarCell($"{v:0.0}", "GB", "99.9", "GB"),
            MetricFormat.Rpm => new BarCell($"{v:0}", "RPM", "9999", "RPM"),
            _ => Rate(v),
        };
    }

    /// <summary>“3.1 KB/s” 拆成数字和单位两部分。</summary>
    static BarCell Rate(float v)
    {
        var s = SensorFormat.Rate(v);
        int i = s.LastIndexOf(' ');
        return new BarCell(s[..i], s[(i + 1)..], "999.9", "MB/s");
    }
}
