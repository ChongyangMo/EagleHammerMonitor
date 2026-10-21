using HardwareType = LibreHardwareMonitor.Hardware.HardwareType;
using SensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace SysMonitor;

static class SensorFormat
{
    public static bool IsPercent(SensorType t) =>
        t is SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity;

    public static string Format(SensorType t, float? value)
    {
        if (value is not float x || float.IsNaN(x) || float.IsInfinity(x))
            return "—";

        return t switch
        {
            SensorType.Temperature => $"{x:0.0} °C",
            SensorType.Voltage => $"{x:0.000} V",
            SensorType.Current => $"{x:0.000} A",
            SensorType.Power => $"{x:0.0} W",
            SensorType.Clock => $"{x:0} MHz",
            SensorType.Frequency => $"{x:0.0} Hz",
            SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => $"{x:0.0} %",
            SensorType.Fan => $"{x:0} RPM",
            SensorType.Flow => $"{x:0.0} L/h",
            SensorType.Data => $"{x:0.00} GB",
            SensorType.SmallData => $"{x:0} MB",
            SensorType.Throughput => Rate(x),
            SensorType.Energy => $"{x:0} mWh",
            SensorType.Noise => $"{x:0.0} dBA",
            SensorType.Conductivity => $"{x:0.0} µS/cm",
            SensorType.Timing => $"{x:0.000} ns",
            SensorType.TimeSpan => Span(x),
            SensorType.Factor => $"{x:0.000}",
            _ => $"{x:0.##}",
        };
    }

    public static string Rate(float bytesPerSec) =>
        bytesPerSec >= 1024 * 1024 * 1024 ? $"{bytesPerSec / (1024f * 1024 * 1024):0.00} GB/s"
        : bytesPerSec >= 1024 * 1024 ? $"{bytesPerSec / (1024f * 1024):0.00} MB/s"
        : bytesPerSec >= 1024 ? $"{bytesPerSec / 1024f:0.0} KB/s"
        : $"{bytesPerSec:0} B/s";

    static string Span(float seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}天 {t:hh\\:mm\\:ss}" : t.ToString(@"hh\:mm\:ss");
    }

    public static string TypeName(SensorType t) => t switch
    {
        SensorType.Temperature => "温度",
        SensorType.Load => "负载",
        SensorType.Clock => "频率",
        SensorType.Power => "功耗",
        SensorType.Voltage => "电压",
        SensorType.Current => "电流",
        SensorType.Fan => "风扇",
        SensorType.Control => "控制",
        SensorType.Data or SensorType.SmallData => "数据量",
        SensorType.Throughput => "速率",
        SensorType.Frequency => "频率",
        SensorType.Flow => "流量",
        SensorType.Level => "电量",
        SensorType.Factor => "系数",
        SensorType.TimeSpan => "时长",
        SensorType.Timing => "时序",
        SensorType.Energy => "能量",
        SensorType.Noise => "噪声",
        SensorType.Conductivity => "电导率",
        SensorType.Humidity => "湿度",
        _ => t.ToString(),
    };

    /// <summary>同一硬件下传感器的显示顺序：温度最先，和 AIDA64 的习惯一致。</summary>
    public static int Order(SensorType t) => t switch
    {
        SensorType.Temperature => 0,
        SensorType.Load => 1,
        SensorType.Clock => 2,
        SensorType.Power => 3,
        SensorType.Voltage => 4,
        SensorType.Current => 5,
        SensorType.Fan => 6,
        SensorType.Control => 7,
        SensorType.Data => 8,
        SensorType.SmallData => 9,
        SensorType.Throughput => 10,
        _ => 20 + (int)t,
    };
}

/// <summary>从快照里挑出状态栏、托盘、默认曲线要用的几个关键传感器。</summary>
static class SensorPicks
{
    static readonly HardwareType[] Gpus = [HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel];

    public static SensorReading? CpuTemp(SensorSnapshot s) =>
        Find(s, HardwareType.Cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Max", "Core Average");

    public static SensorReading? CpuLoad(SensorSnapshot s) => Find(s, HardwareType.Cpu, SensorType.Load, "CPU Total");

    // LHM 有 “Total Memory” 和 “Virtual Memory” 两组，负载传感器都叫 Memory，这里要物理内存那组
    public static SensorReading? MemLoad(SensorSnapshot s) =>
        s.Readings.FirstOrDefault(r => r.HardwareType == HardwareType.Memory && r.Type == SensorType.Load
            && r.Name == "Memory" && !r.HardwareName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
        ?? Find(s, HardwareType.Memory, SensorType.Load, "Memory");

    /// <summary>显存占用（%），与 <see cref="GpuLoad"/> 同一块显卡；核显、部分 AMD 卡没有这个传感器。</summary>
    public static SensorReading? GpuMemLoad(SensorSnapshot s) =>
        GpuLoad(s) is { } gpu
            ? s.Readings.FirstOrDefault(r => r.HardwareId == gpu.HardwareId && r.Type == SensorType.Load && r.Name == "GPU Memory")
            : null;

    /// <summary>所有网卡的上传、下载速率之和（字节/秒）；没有网卡数据时为 null。</summary>
    public static (float Up, float Down)? NetRates(SensorSnapshot s)
    {
        float up = 0, down = 0;
        bool any = false;
        foreach (var r in s.Readings)
        {
            if (r.HardwareType != HardwareType.Network || r.Type != SensorType.Throughput || r.Value is not float v)
                continue;
            if (r.Name == "Upload Speed")
                up += v;
            else if (r.Name == "Download Speed")
                down += v;
            else
                continue;
            any = true;
        }
        return any ? (up, down) : null;
    }

    // 独显优先于核显
    public static SensorReading? GpuLoad(SensorSnapshot s) =>
        Gpus.Select(h => Find(s, h, SensorType.Load, "GPU Core", "D3D 3D")).FirstOrDefault(r => r != null);

    public static SensorReading? GpuTemp(SensorSnapshot s) =>
        Gpus.Select(h => Find(s, h, SensorType.Temperature, "GPU Core", "GPU Hot Spot")).FirstOrDefault(r => r != null);

    static SensorReading? Find(SensorSnapshot s, HardwareType hw, SensorType type, params string[] preferred)
    {
        SensorReading? first = null, firstWithValue = null;
        foreach (var r in s.Readings)
        {
            if (r.HardwareType != hw || r.Type != type)
                continue;
            first ??= r;
            if (r.Value.HasValue)
                firstWithValue ??= r;
        }
        if (first == null)
            return null;

        foreach (var name in preferred)
        {
            var match = s.Readings.FirstOrDefault(r => r.HardwareType == hw && r.Type == type && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }
        return firstWithValue ?? first;
    }

    /// <summary>状态栏/托盘提示用的几段简短文字。</summary>
    public static List<string> QuickStats(SensorSnapshot s)
    {
        var parts = new List<string>();
        void Part(string label, params SensorReading?[] readings)
        {
            var vals = readings.Where(r => r?.Value != null).Select(r => SensorFormat.Format(r!.Type, r.Value)).ToList();
            if (vals.Count > 0)
                parts.Add($"{label} {string.Join(" · ", vals)}");
        }

        Part("CPU", CpuLoad(s), CpuTemp(s));
        Part("内存", MemLoad(s));
        Part("GPU", GpuLoad(s), GpuTemp(s));
        return parts;
    }
}
