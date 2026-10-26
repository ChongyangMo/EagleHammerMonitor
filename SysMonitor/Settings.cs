using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SysMonitor;

public enum ThemeChoice { System, Light, Dark }

/// <summary>任务栏数据放在居中图标组的哪一侧。</summary>
public enum TaskbarSide { Left, Right }

/// <summary>任务栏上显示哪些项目（默认全部显示）。</summary>
public sealed record TaskbarItems
{
    public bool CpuLoad { get; init; } = true;
    public bool CpuTemp { get; init; } = true;
    public bool MemLoad { get; init; } = true;
    public bool GpuLoad { get; init; } = true;
    public bool GpuTemp { get; init; } = true;
    public bool GpuMemLoad { get; init; } = true;
    public bool NetUp { get; init; } = true;
    public bool NetDown { get; init; } = true;

    /// <summary>占用率数字前的小条。</summary>
    public bool Meters { get; init; } = true;

    /// <summary>三块从左到右的顺序（编辑窗口里拖动调整）。</summary>
    public TaskbarBlock[]? Order { get; init; }

    /// <summary>整理过的顺序：去掉重复和无效的，漏掉的块按默认顺序补在后面。</summary>
    public TaskbarBlock[] BlockOrder() =>
        [.. (Order ?? []).Where(b => Enum.IsDefined(b)).Concat(Enum.GetValues<TaskbarBlock>()).Distinct()];
}

/// <summary>任务栏上的三块。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskbarBlock>))]
public enum TaskbarBlock { Cpu, Gpu, Net }

/// <summary>需要记住的设置，存在 %AppData%\SysMonitor\settings.json。</summary>
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

    public TaskbarItems TaskbarItems { get; set; } = new();

    /// <summary>传感器分组的展开状态（键为硬件 Id）。没记录过的分组默认折叠。</summary>
    public Dictionary<string, bool> SensorGroups { get; set; } = [];

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SysMonitor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
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
