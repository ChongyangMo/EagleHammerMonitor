using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SysMonitor;

/// <summary>
/// 把 LibreHardwareMonitor 的英文传感器 / 硬件名翻译成中文，只用于显示。
/// 内部（挑选默认曲线、状态栏汇总等）仍按英文原名匹配，见 <see cref="SensorPicks"/>。
/// 规则：先查固定名称；再按“内存时序 / 带编号 / 带括号 / 末尾数字”拆开分别翻译；都不匹配就保留原文。
/// </summary>
public static partial class SensorNames
{
    static readonly ConcurrentDictionary<string, string> Cache = new();

    static readonly Dictionary<string, string> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── CPU ──
        ["CPU Total"] = "CPU 总负载",
        ["CPU Core Max"] = "CPU 最高核心负载",
        ["CPU Package"] = "CPU 封装",
        ["Package"] = "封装",
        ["Core"] = "核心",
        ["CPU Core"] = "CPU 核心",
        ["CPU Cores"] = "CPU 核心",
        ["Core Max"] = "核心最高",
        ["Core Average"] = "核心平均",
        ["Cores (Average)"] = "全部核心（平均）",
        ["Cores (Average Effective)"] = "全部核心（平均有效频率）",
        ["CCDs Max"] = "CCD 最高",
        ["CCDs Average"] = "CCD 平均",
        ["Bus Speed"] = "总线频率",
        ["Fabric"] = "Infinity Fabric",
        ["Uncore"] = "非核心（Uncore）",
        ["TDC"] = "TDC 电流",
        ["EDC"] = "EDC 电流",
        ["PPT"] = "PPT 功率",
        ["CPU Graphics"] = "CPU 核显",
        ["CPU Memory"] = "CPU 内存控制器",
        ["IA Cores"] = "IA 核心",
        ["GT Cores"] = "GT 核显",
        ["System Agent"] = "系统代理",
        ["P-Core"] = "性能核",
        ["E-Core"] = "能效核",
        ["Distance to TjMax"] = "距 TjMax",
        ["Core Distance to TjMax"] = "核心距 TjMax",
        ["Thread"] = "线程",

        // ── 主板 Super I/O ──
        ["Vcore"] = "核心电压（Vcore）",
        ["AVCC"] = "模拟电源（AVCC）",
        ["+3V Standby"] = "+3V 待机",
        ["CMOS Battery"] = "CMOS 电池",
        ["CPU Termination"] = "CPU 终端电压",
        ["DRAM"] = "内存电压",
        ["Chipset"] = "芯片组",
        ["CPU"] = "CPU",
        ["System"] = "系统",
        ["Temperature"] = "温度",
        ["Voltage"] = "电压",
        ["Fan"] = "风扇",
        ["Fan Control"] = "风扇控制",
        ["Auxiliary"] = "辅助",

        // ── 内存 ──
        ["Memory"] = "内存占用",
        ["Memory Used"] = "已用内存",
        ["Memory Available"] = "可用内存",
        ["Virtual Memory"] = "虚拟内存占用",
        ["Virtual Memory Used"] = "已用虚拟内存",
        ["Virtual Memory Available"] = "可用虚拟内存",
        ["Capacity"] = "容量",

        // ── 显卡 ──
        ["GPU Core"] = "GPU 核心",
        ["GPU Hot Spot"] = "GPU 热点",
        ["GPU Memory Junction"] = "显存结温",
        ["GPU Memory"] = "显存",
        ["GPU Memory Controller"] = "显存控制器",
        ["GPU Video Engine"] = "视频引擎",
        ["GPU Bus"] = "总线接口",
        ["GPU Power"] = "GPU 功耗占比",
        ["GPU Board Power"] = "整卡功耗占比",
        ["GPU Package"] = "GPU 封装",
        ["GPU Core Voltage"] = "GPU 核心电压",
        ["GPU Memory Free"] = "空闲显存",
        ["GPU Memory Used"] = "已用显存",
        ["GPU Memory Total"] = "显存总量",
        ["GPU PCIe Rx"] = "PCIe 接收",
        ["GPU PCIe Tx"] = "PCIe 发送",
        ["GPU Fan"] = "GPU 风扇",
        ["GPU Shader"] = "着色器",
        ["GPU SoC"] = "GPU SoC",
        ["GPU Frame Buffer"] = "帧缓冲",
        ["D3D 3D"] = "D3D 3D 渲染",
        ["D3D Copy"] = "D3D 复制",
        ["D3D Compute"] = "D3D 计算",
        ["D3D Video Decode"] = "D3D 视频解码",
        ["D3D Video Encode"] = "D3D 视频编码",
        ["D3D Video Processing"] = "D3D 视频处理",
        ["D3D Overlay"] = "D3D 叠加层",
        ["D3D Security"] = "D3D 安全",
        ["D3D VR"] = "D3D VR",
        ["D3D Optical Flow Accelerator"] = "D3D 光流加速器",
        ["D3D Dedicated Memory Used"] = "D3D 专用显存占用",
        ["D3D Shared Memory Used"] = "D3D 共享显存占用",
        ["D3D Dedicated Memory Free"] = "D3D 专用显存空闲",
        ["D3D Dedicated Memory Total"] = "D3D 专用显存总量",

        // ── 存储 ──
        ["Composite Temperature"] = "综合温度",
        ["Warning Temperature"] = "警告温度阈值",
        ["Critical Temperature"] = "临界温度阈值",
        ["Available Spare"] = "可用备用空间",
        ["Available Spare Threshold"] = "备用空间阈值",
        ["Percentage Used"] = "寿命已用",
        ["Life"] = "剩余寿命",
        ["Remaining Life"] = "剩余寿命",
        ["Power On Count"] = "通电次数",
        ["Power On Hours"] = "通电时间",
        ["Power Cycle Count"] = "通电次数",
        ["Read Activity"] = "读取活动",
        ["Write Activity"] = "写入活动",
        ["Total Activity"] = "总活动",
        ["Read Rate"] = "读取速度",
        ["Write Rate"] = "写入速度",
        ["Data Read"] = "累计读取",
        ["Data Written"] = "累计写入",
        ["Used Space"] = "已用空间",
        ["Free Space"] = "可用空间",
        ["Total Space"] = "总空间",
        ["Write Amplification"] = "写入放大",
        ["Media Errors"] = "介质错误",
        ["Unsafe Shutdowns"] = "异常关机次数",
        ["Error Info Log Count"] = "错误日志数",
        ["Host Reads"] = "主机读取",
        ["Host Writes"] = "主机写入",
        ["Airflow Temperature"] = "气流温度",

        // ── 网络 ──
        ["Network Utilization"] = "网络利用率",
        ["Data Uploaded"] = "累计上传",
        ["Data Downloaded"] = "累计下载",
        ["Upload Speed"] = "上传速度",
        ["Download Speed"] = "下载速度",

        // ── 电池 / 电源 ──
        ["Charge Level"] = "电量",
        ["Degradation Level"] = "损耗程度",
        ["Designed Capacity"] = "设计容量",
        ["Full Charged Capacity"] = "满电容量",
        ["Fully-Charged Capacity"] = "满电容量",
        ["Remaining Capacity"] = "剩余容量",
        ["Charge Current"] = "充电电流",
        ["Discharge Current"] = "放电电流",
        ["Charge Rate"] = "充电功率",
        ["Discharge Rate"] = "放电功率",
        ["Charge/Discharge Current"] = "充放电电流",
        ["Charge/Discharge Rate"] = "充放电功率",
        ["Remaining Time (Estimated)"] = "预计剩余时间",
        ["Power"] = "功率",
        ["Current"] = "电流",
    };

    /// <summary>括号里的补充说明。</summary>
    static readonly Dictionary<string, string> Paren = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Effective"] = "有效频率",
        ["Average"] = "平均",
        ["Average Effective"] = "平均有效频率",
        ["Max"] = "最高",
        ["Min"] = "最低",
    };

    /// <summary>内存 SPD 时序的说明部分（缩写本身保留，是业内通用叫法）。</summary>
    static readonly Dictionary<string, string> Timing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CAS Latency Time"] = "CAS 延迟",
        ["RAS to CAS Delay Time"] = "行到列延迟",
        ["Row Precharge Delay Time"] = "行预充电时间",
        ["Active to Precharge Delay Time"] = "行激活到预充电",
        ["Active to Active/Refresh Delay Time"] = "行周期（激活到激活/刷新）",
        ["Refresh Recovery Delay Time"] = "刷新恢复时间",
        ["Four Activate Window Time"] = "四次激活窗口",
        ["Activate to Activate Delay Time"] = "行激活间隔",
        ["CAS to CAS Delay Time"] = "列到列延迟",
        ["Write Recovery Time"] = "写恢复时间",
        ["Write to Read Time"] = "写到读延迟",
        ["Write to Read Delay Time"] = "写到读延迟",
        ["Minimum Cycle Time"] = "最小时钟周期",
        ["Maximum Cycle Time"] = "最大时钟周期",
    };

    /// <summary>通用的硬件名（具体型号如 “AMD Ryzen 9 5950X” 不翻译）。</summary>
    static readonly Dictionary<string, string> Hardware = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Total Memory"] = "物理内存",
        ["Virtual Memory"] = "虚拟内存",
        ["Generic Memory"] = "内存",
        ["Battery"] = "电池",
    };

    public static string Translate(string name) => Cache.GetOrAdd(name, Compute);

    public static string TranslateHardware(string name, LibreHardwareMonitor.Hardware.HardwareType type)
    {
        if (Hardware.TryGetValue(name, out var t))
            return t;

        // 内存条（SPD）：“厂商 - 型号 (#0)” → “内存条 #1 · 型号（厂商）”，编号从 1 开始
        if (type == LibreHardwareMonitor.Hardware.HardwareType.Memory)
        {
            var m = DimmPattern().Match(name);
            if (m.Success)
                return $"内存条 #{int.Parse(m.Groups[3].Value) + 1} · {m.Groups[2].Value.Trim()}（{m.Groups[1].Value.Trim()}）";
        }
        return name;
    }

    [GeneratedRegex(@"^(.+) - (.+) \(#(\d+)\)$")]
    private static partial Regex DimmPattern();

    static string Compute(string name)
    {
        if (Exact.TryGetValue(name, out var t))
            return t;

        // 内存时序：tRAS (Active to Precharge Delay Time) → tRAS（行激活到预充电）
        var m = TimingPattern().Match(name);
        if (m.Success)
            return $"{m.Groups[1].Value}（{Lookup(Timing, m.Groups[2].Value)}）";

        // 带编号：Core #1 (Effective) → 核心 #1（有效频率）；Core #1 VID → 核心 #1 VID
        m = NumberPattern().Match(name);
        if (m.Success)
            return $"{Base(m.Groups[1].Value)} #{m.Groups[2].Value}{Suffix(m.Groups[3].Value)}";

        // 带括号：Core (Tctl/Tdie) → 核心（Tctl/Tdie）；CCDs Max (Tdie) → CCD 最高（Tdie）
        m = ParenPattern().Match(name);
        if (m.Success)
            return $"{Base(m.Groups[1].Value)}（{Lookup(Paren, m.Groups[2].Value)}）";

        // 末尾数字：GPU Fan 1 → GPU 风扇 1；Temperature 2 → 温度 2
        m = TrailingNumberPattern().Match(name);
        if (m.Success && Exact.TryGetValue(m.Groups[1].Value, out t))
            return $"{t} {m.Groups[2].Value}";

        return name;
    }

    static string Base(string s) => Exact.TryGetValue(s, out var t) ? t : s;

    static string Lookup(Dictionary<string, string> dict, string s) => dict.TryGetValue(s, out var t) ? t : s;

    static string Suffix(string rest)
    {
        rest = rest.Trim();
        if (rest.Length == 0)
            return "";
        if (rest.StartsWith('(') && rest.EndsWith(')'))
            return $"（{Lookup(Paren, rest[1..^1])}）";
        return " " + rest;
    }

    [GeneratedRegex(@"^(t[A-Za-z0-9_]+) \((.+)\)$")]
    private static partial Regex TimingPattern();

    [GeneratedRegex(@"^(.+?) #(\d+)(.*)$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^(.+?) \((.+)\)$")]
    private static partial Regex ParenPattern();

    [GeneratedRegex(@"^(.+?) (\d+)$")]
    private static partial Regex TrailingNumberPattern();
}
