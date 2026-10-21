using System.Text;

namespace SysMonitor;

static class Report
{
    public static string Build(IReadOnlyList<InfoPage> pages, SensorSnapshot? sensors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SysMonitor 系统报告");
        sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"计算机：{Environment.MachineName}");

        foreach (var page in pages)
        {
            Section(sb, page.Title);
            foreach (var g in page.Groups)
            {
                sb.AppendLine();
                sb.AppendLine($"[{g.Title}]");
                foreach (var item in g.Items)
                    sb.AppendLine("  " + Pad(item.Name, 26) + item.Value);
            }
        }

        Section(sb, "传感器");
        if (sensors == null)
        {
            sb.AppendLine("（无传感器数据）");
        }
        else
        {
            sb.AppendLine($"采样时间：{sensors.Time:HH:mm:ss}");
            foreach (var hw in sensors.Readings.Where(r => r.Value.HasValue || r.Max.HasValue).GroupBy(r => r.HardwareId))
            {
                sb.AppendLine();
                sb.AppendLine($"[{hw.First().HardwareDisplayName}]");
                sb.AppendLine("  " + Pad("传感器", 40) + Pad("当前", 16) + Pad("最小", 16) + "最大");
                foreach (var r in hw)
                {
                    sb.AppendLine("  " + Pad($"{r.DisplayName}（{SensorFormat.TypeName(r.Type)}）", 40)
                        + Pad(SensorFormat.Format(r.Type, r.Value), 16)
                        + Pad(SensorFormat.Format(r.Type, r.Min), 16)
                        + SensorFormat.Format(r.Type, r.Max));
                }
            }
        }
        return sb.ToString();
    }

    static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine(new string('=', 72));
        sb.AppendLine("  " + title);
        sb.AppendLine(new string('=', 72));
    }

    /// <summary>按等宽字体下的显示宽度补空格（中文占两格）。</summary>
    static string Pad(string s, int width)
    {
        int w = s.Sum(ch => ch >= 0x2E80 ? 2 : 1);
        return s + new string(' ', Math.Max(1, width - w));
    }

    public static int RunHeadless(string path)
    {
        var pages = SystemInfo.CollectAll();
        SensorSnapshot? sensors = null;
        string? error = null;
        try { sensors = SensorService.CaptureOnce(); }
        catch (Exception ex) { error = ex.Message; }

        var text = Build(pages, sensors);
        if (error != null)
            text += Environment.NewLine + "传感器读取失败：" + error + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return 0;
    }
}
