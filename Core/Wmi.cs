using System.Management;

namespace EagleHammerMonitor;

static class Wmi
{
    public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (var o in searcher.Get())
                list.Add(o);
        }
        catch
        {
            // 命名空间不存在 / 无权限 / 超时：当作没有数据
        }
        return list;
    }

    public static object? Raw(this ManagementBaseObject o, string prop)
    {
        try { return o[prop]; }
        catch { return null; }
    }

    public static string? Str(this ManagementBaseObject o, string prop) => o.Raw(prop) switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        Array a => string.Join(", ", a.Cast<object?>().Select(x => x?.ToString()?.Trim()).Where(x => !string.IsNullOrEmpty(x))),
        var v => v.ToString(),
    };

    public static ulong? U64(this ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o.Raw(prop);
            return v == null ? null : Convert.ToUInt64(v);
        }
        catch { return null; }
    }

    public static bool? Bool(this ManagementBaseObject o, string prop) => o.Raw(prop) is bool b ? b : null;

    public static DateTime? Date(this ManagementBaseObject o, string prop)
    {
        var s = o.Str(prop);
        if (s == null)
            return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
