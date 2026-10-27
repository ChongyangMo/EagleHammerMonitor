using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace EagleHammerMonitor;

public enum ThemeChoice { System, Light, Dark }

/// <summary>任务栏数据放在居中图标组的哪一侧。</summary>
public enum TaskbarSide { Left, Right }

/// <summary>任务栏上的一行（每种只有一行），显示什么见 TaskbarCatalog。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskbarRowKind>))]
public enum TaskbarRowKind { Cpu, Mem, Gpu, Vram, Up, Down, Disk, Fan }

/// <summary>行里可以显示的一项数据。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskbarMetric>))]
public enum TaskbarMetric
{
    CpuLoad, CpuTemp, CpuClock, CpuPower,
    MemLoad, MemUsed,
    GpuLoad, GpuTemp, GpuClock, GpuPower, GpuFan,
    VramLoad, VramUsed,
    NetUp, NetDown,
    DiskActivity, DiskTemp, DiskRead, DiskWrite,
}

/// <summary>上传/下载行的标签：箭头“↑”、文字“上传”，或两者都有“↑ 上传”。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NetLabelStyle>))]
public enum NetLabelStyle { Arrow, Text, Both }

/// <summary>一行的设置：显示哪几项数据；风扇行另外记着显示哪几个风扇（传感器 Id）。</summary>
public sealed class TaskbarRow
{
    public TaskbarRowKind Kind { get; set; }
    public List<TaskbarMetric> Metrics { get; set; } = [];
    public List<string> Fans { get; set; } = [];
}

/// <summary>
/// 任务栏布局：若干列从左到右排，每列最多两行（任务栏只有两行高）。
/// 不显示的行放在 Hidden 里，记着各自的选项，重新拖回来时恢复。
/// </summary>
public sealed class TaskbarLayout
{
    public const int MaxRows = 2;

    public List<List<TaskbarRow>> Columns { get; set; } = [];
    public List<TaskbarRow> Hidden { get; set; } = [];

    /// <summary>占用率数字前的小条。</summary>
    public bool Meters { get; set; } = true;

    public NetLabelStyle NetLabel { get; set; } = NetLabelStyle.Arrow;

    public TaskbarLayout Clone() => JsonSerializer.Deserialize<TaskbarLayout>(JsonSerializer.Serialize(this))!;

    /// <summary>
    /// 整理（读取设置后、编辑完成后调用）：去掉空列、重复的行和不认识的数据项，
    /// 一列超过两行的挪到后面新的一列，漏掉的行放进 Hidden。
    /// </summary>
    public TaskbarLayout Normalize()
    {
        var seen = new HashSet<TaskbarRowKind>();
        bool Keep(TaskbarRow? r)
        {
            if (r == null || !Enum.IsDefined(r.Kind) || !seen.Add(r.Kind))
                return false;
            var allowed = TaskbarCatalog.Row(r.Kind).Metrics.Select(m => m.Metric).ToHashSet();
            r.Metrics = [.. (r.Metrics ?? []).Where(allowed.Contains).Distinct()];
            r.Fans = [.. (r.Fans ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct()];
            return true;
        }

        var columns = new List<List<TaskbarRow>>();
        foreach (var col in Columns ?? [])
        {
            var rows = (col ?? []).Where(Keep).ToList();
            for (int i = 0; i < rows.Count; i += MaxRows)
                columns.Add(rows.GetRange(i, Math.Min(MaxRows, rows.Count - i)));
        }
        Columns = columns;
        Hidden = [.. (Hidden ?? []).Where(Keep)];
        foreach (var kind in Enum.GetValues<TaskbarRowKind>())
            if (seen.Add(kind))
                Hidden.Add(TaskbarCatalog.NewRow(kind));
        if (!Enum.IsDefined(NetLabel))
            NetLabel = NetLabelStyle.Arrow;
        return this;
    }
}

/// <summary>需要记住的设置，存在 %AppData%\EagleHammerMonitor\settings.json。</summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<ThemeChoice>))]
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public int IntervalMs { get; set; } = 1000;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>点窗口的 × 时隐藏到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>在任务栏空白处显示 CPU / 内存 / GPU / 网速（仅 Win11）。</summary>
    public bool TaskbarDisplay { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<TaskbarSide>))]
    public TaskbarSide TaskbarSide { get; set; } = TaskbarSide.Right;

    public TaskbarLayout TaskbarLayout { get; set; } = TaskbarCatalog.DefaultLayout();

    /// <summary>传感器分组的展开状态（键为硬件 Id）。没记录过的分组默认折叠。</summary>
    public Dictionary<string, bool> SensorGroups { get; set; } = [];

    static string FilePath => PathIn("EagleHammerMonitor");

    /// <summary>改名前的位置。新位置还没有设置文件时从这里读，下次保存就写到新位置。</summary>
    static string LegacyFilePath => PathIn("SysMonitor");

    static string PathIn(string folder) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string file = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (File.Exists(file) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) is { } s)
            {
                s.TaskbarLayout = s.TaskbarLayout?.Normalize() ?? TaskbarCatalog.DefaultLayout();
                return s;
            }
        }
        catch
        {
            // 文件损坏就用默认值
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

static class ThemeHelper
{
    public static System.Windows.ThemeMode ToWpf(this ThemeChoice c) => c switch
    {
        ThemeChoice.Light => System.Windows.ThemeMode.Light,
        ThemeChoice.Dark => System.Windows.ThemeMode.Dark,
        _ => System.Windows.ThemeMode.System,
    };

    /// <summary>实际生效的是否深色（“跟随系统”时读系统设置）。</summary>
    public static bool IsDark(ThemeChoice c) => c == ThemeChoice.Dark || (c == ThemeChoice.System && SystemPrefersDark());

    static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
