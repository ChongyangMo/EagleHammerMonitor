using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SysMonitor;

public enum ThemeChoice { System, Light, Dark }

/// <summary>任务栏数据放在居中图标组的哪一侧。</summary>
public enum TaskbarSide { Left, Right }

/// <summary>需要记住的设置，存在 %AppData%\SysMonitor\settings.json。</summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<ThemeChoice>))]
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public int IntervalMs { get; set; } = 1000;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>在任务栏空白处显示 CPU / 内存 / GPU / 网速（仅 Win11）。</summary>
    public bool TaskbarDisplay { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<TaskbarSide>))]
    public TaskbarSide TaskbarSide { get; set; } = TaskbarSide.Right;

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
