namespace SysMonitor;

public sealed record InfoItem(string Name, string Value);

public sealed class InfoGroup(string title)
{
    // 厂商没填写时 SMBIOS 里常见的占位文字，显示出来没有意义
    static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.", "Default string", "Not Specified", "Not Applicable", "None", "Unknown",
        // 华硕等主板 BIOS 里的模板占位值
        "System Product Name", "System manufacturer", "System Version", "System Serial Number", "SKU", "Rev X.0x",
    };

    public string Title { get; } = title;
    public List<InfoItem> Items { get; } = [];

    public void Add(string name, string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || Junk.Contains(value))
            return;
        Items.Add(new InfoItem(name, value));
    }
}

public sealed class InfoPage(string title)
{
    public string Title { get; } = title;
    public List<InfoGroup> Groups { get; } = [];

    public InfoGroup Group(string title)
    {
        var g = new InfoGroup(title);
        Groups.Add(g);
        return g;
    }
}

static class Fmt
{
    static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(double b)
    {
        int i = 0;
        while (b >= 1024 && i < ByteUnits.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return i == 0 ? $"{b:0} B" : $"{b:0.##} {ByteUnits[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} 天 {t.Hours} 小时 {t.Minutes} 分"
        : t.TotalHours >= 1 ? $"{t.Hours} 小时 {t.Minutes} 分"
        : $"{t.Minutes} 分 {t.Seconds} 秒";

    public static string? Date(DateTime? d) => d?.ToString("yyyy-MM-dd");

    public static string? DateTime(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss");

    public static string BitRate(long bps) => bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0.#} Mbps";

    /// <summary>用空格拼接非空片段。</summary>
    public static string Join(params string?[] parts) =>
        string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
}
