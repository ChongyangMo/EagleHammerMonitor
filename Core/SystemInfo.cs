using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Arm = System.Runtime.Intrinsics.Arm;
using X86 = System.Runtime.Intrinsics.X86;

namespace EagleHammerMonitor;

/// <summary>收集静态硬件/系统信息（WMI、注册表、.NET API），生成各分类页面。</summary>
public sealed class SystemInfo
{
    sealed record DiskInfo(string Model, ulong Size, string? Bus, string? Media, string? Serial, string? Firmware, string? Health);

    sealed record MonitorInfo(string Name, string? Manufacturer, string? Serial, int? Year, double? DiagonalInch);

    const string StorageScope = @"root\Microsoft\Windows\Storage";

    readonly Dictionary<string, List<ManagementBaseObject>> _cache = [];
    List<DiskInfo>? _disks;
    List<MonitorInfo>? _monitors;
    Dictionary<string, ulong>? _vram;
    Dictionary<string, bool>? _adapters;
    MemoryTimingInfo? _timings;
    bool _timingsRead;

    public static List<InfoPage> CollectAll() => new SystemInfo().Collect();

    List<InfoPage> Collect()
    {
        var builders = new (string Title, Action<InfoPage> Build)[]
        {
            ("系统摘要", SummaryPage),
            ("操作系统", OperatingSystemPage),
            ("处理器", ProcessorPage),
            ("主板", MotherboardPage),
            ("内存", MemoryPage),
            ("显示设备", DisplayPage),
            ("存储", StoragePage),
            ("网络", NetworkPage),
        };

        var pages = new List<InfoPage>();
        foreach (var (title, build) in builders)
        {
            var page = new InfoPage(title);
            try { build(page); }
            catch (Exception ex) { page.Group("错误").Add("收集失败", ex.Message); }
            pages.Add(page);
        }
        return pages;
    }

    List<ManagementBaseObject> Q(string cls, string scope = @"root\cimv2")
    {
        string key = scope + ":" + cls;
        if (!_cache.TryGetValue(key, out var list))
            _cache[key] = list = Wmi.Query("SELECT * FROM " + cls, scope);
        return list;
    }

    ManagementBaseObject? One(string cls, string scope = @"root\cimv2") => Q(cls, scope).FirstOrDefault();

    // ───────────────────────────── 系统摘要 ─────────────────────────────

    void SummaryPage(InfoPage p)
    {
        var g = p.Group("计算机");
        g.Add("操作系统", Fmt.Join(One("Win32_OperatingSystem")?.Str("Caption")?.Replace("Microsoft ", ""), WinVersion()));
        g.Add("计算机名", Environment.MachineName);
        g.Add("用户名", Environment.UserName);
        g.Add("系统运行时间", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));

        g = p.Group("主板");
        foreach (var cpu in Q("Win32_Processor"))
            g.Add("处理器", $"{cpu.Str("Name")}（{cpu.Str("NumberOfCores")} 核 / {cpu.Str("NumberOfLogicalProcessors")} 线程）");
        var board = One("Win32_BaseBoard");
        g.Add("主板", Fmt.Join(board?.Str("Manufacturer"), board?.Str("Product")));
        var bios = One("Win32_BIOS");
        g.Add("BIOS", Fmt.Join(bios?.Str("Manufacturer"), bios?.Str("SMBIOSBIOSVersion"), Fmt.Date(bios?.Date("ReleaseDate"))));
        g.Add("系统内存", MemorySummary());

        g = p.Group("显示");
        foreach (var gpu in Q("Win32_VideoController"))
        {
            var name = gpu.Str("Name");
            ulong vram = Vram(gpu);
            g.Add("显示适配器", vram > 0 ? $"{name}（{Fmt.Bytes(vram)}）" : name);
        }
        foreach (var m in Monitors())
            g.Add("显示器", m.DiagonalInch is double d ? $"{m.Name}（{d:0.#} 英寸）" : m.Name);

        g = p.Group("存储");
        foreach (var d in Disks())
            g.Add("磁盘", $"{d.Model}（{Fmt.Bytes(d.Size)}{(d.Bus == null ? "" : "，" + d.Bus)}{(d.Media == null ? "" : " " + d.Media)}）");
        foreach (var v in Volumes())
            g.Add("分区 " + v.Name.TrimEnd('\\'), $"{v.DriveFormat}，{Fmt.Bytes(v.TotalSize)}（可用 {Fmt.Bytes(v.AvailableFreeSpace)}）");

        g = p.Group("网络");
        foreach (var n in Nics().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            var ipv4 = Ips(n, AddressFamily.InterNetwork);
            g.Add("网络适配器", string.IsNullOrEmpty(ipv4) ? n.Description : $"{n.Description}（{ipv4}）");
        }
    }

    // ───────────────────────────── 操作系统 ─────────────────────────────

    void OperatingSystemPage(InfoPage p)
    {
        var os = One("Win32_OperatingSystem");
        var cs = One("Win32_ComputerSystem");

        var g = p.Group("Windows");
        g.Add("名称", os?.Str("Caption"));
        g.Add("版本", WinVersion());
        g.Add("体系结构", os?.Str("OSArchitecture"));
        g.Add("安装日期", Fmt.DateTime(os?.Date("InstallDate")));
        g.Add("上次启动", Fmt.DateTime(os?.Date("LastBootUpTime")));
        g.Add("运行时间", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));
        g.Add("注册用户", os?.Str("RegisteredUser"));
        g.Add("产品 ID", os?.Str("SerialNumber"));
        g.Add("Windows 目录", os?.Str("WindowsDirectory"));
        g.Add("系统目录", os?.Str("SystemDirectory"));

        g = p.Group("环境");
        g.Add("计算机名", Environment.MachineName);
        g.Add("用户名", Environment.UserName);
        bool domain = cs?.Bool("PartOfDomain") == true;
        g.Add(domain ? "域" : "工作组", domain ? cs?.Str("Domain") : cs?.Str("Workgroup"));
        g.Add("区域", CultureInfo.CurrentCulture.DisplayName);
        g.Add("时区", TimeZoneInfo.Local.DisplayName);
        g.Add(".NET 运行时", RuntimeInformation.FrameworkDescription);
    }

    static string? WinVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k == null)
            return null;
        var display = k.GetValue("DisplayVersion") as string;
        var build = k.GetValue("CurrentBuild") as string;
        if (build != null && k.GetValue("UBR") is int ubr)
            build += "." + ubr;
        if (build == null)
            return display;
        return display == null ? $"内部版本 {build}" : $"{display}（内部版本 {build}）";
    }

    // ───────────────────────────── 处理器 ─────────────────────────────

    void ProcessorPage(InfoPage p)
    {
        bool hypervisor = One("Win32_ComputerSystem")?.Bool("HypervisorPresent") == true;
        foreach (var cpu in Q("Win32_Processor"))
        {
            var g = p.Group(cpu.Str("Name") ?? "处理器");
            g.Add("名称", cpu.Str("Name"));
            g.Add("制造商", cpu.Str("Manufacturer"));
            g.Add("架构", Arch(cpu.U64("Architecture")));
            g.Add("插槽", cpu.Str("SocketDesignation"));
            g.Add("物理核心", cpu.Str("NumberOfCores"));
            g.Add("逻辑处理器", cpu.Str("NumberOfLogicalProcessors"));
            g.Add("最大频率", cpu.U64("MaxClockSpeed") is > 0 and var mhz ? $"{mhz} MHz" : null);
            g.Add("二级缓存", Kb(cpu.U64("L2CacheSize")));
            g.Add("三级缓存", Kb(cpu.U64("L3CacheSize")));
            g.Add("标识", cpu.Str("Description"));
            g.Add("处理器 ID", cpu.Str("ProcessorId"));
            g.Add("硬件虚拟化", hypervisor ? "已启用（Hypervisor 运行中）"
                : cpu.Bool("VirtualizationFirmwareEnabled") switch { true => "已启用", false => "未启用", null => null });
        }

        var isa = InstructionSets();
        if (!string.IsNullOrEmpty(isa))
            p.Group("指令集").Add("支持的扩展", isa);
    }

    static string? Arch(ulong? a) => a switch
    {
        0 => "x86", 5 => "ARM", 6 => "IA-64", 9 => "x64", 12 => "ARM64", _ => null,
    };

    static string? Kb(ulong? kb) => kb is > 0 ? Fmt.Bytes(kb.Value * 1024d) : null;

    static string InstructionSets()
    {
        var list = new List<string>();
        void A(bool ok, string name)
        {
            if (ok) list.Add(name);
        }

        if (X86.X86Base.IsSupported)
        {
            A(X86.Sse.IsSupported, "SSE");
            A(X86.Sse2.IsSupported, "SSE2");
            A(X86.Sse3.IsSupported, "SSE3");
            A(X86.Ssse3.IsSupported, "SSSE3");
            A(X86.Sse41.IsSupported, "SSE4.1");
            A(X86.Sse42.IsSupported, "SSE4.2");
            A(X86.Popcnt.IsSupported, "POPCNT");
            A(X86.Avx.IsSupported, "AVX");
            A(X86.Avx2.IsSupported, "AVX2");
            A(X86.Fma.IsSupported, "FMA3");
            A(X86.Bmi1.IsSupported, "BMI1");
            A(X86.Bmi2.IsSupported, "BMI2");
            A(X86.Lzcnt.IsSupported, "LZCNT");
            A(X86.Aes.IsSupported, "AES-NI");
            A(X86.Pclmulqdq.IsSupported, "PCLMULQDQ");
            A(X86.AvxVnni.IsSupported, "AVX-VNNI");
            A(X86.Avx512F.IsSupported, "AVX-512F");
            A(X86.Avx512BW.IsSupported, "AVX-512BW");
        }
        else if (Arm.ArmBase.IsSupported)
        {
            A(Arm.AdvSimd.IsSupported, "NEON");
            A(Arm.Aes.IsSupported, "AES");
            A(Arm.Crc32.IsSupported, "CRC32");
            A(Arm.Sha1.IsSupported, "SHA1");
            A(Arm.Sha256.IsSupported, "SHA256");
            A(Arm.Dp.IsSupported, "DotProd");
            A(Arm.Rdm.IsSupported, "RDM");
        }
        return string.Join(", ", list);
    }

    // ───────────────────────────── 主板 ─────────────────────────────

    void MotherboardPage(InfoPage p)
    {
        var cs = One("Win32_ComputerSystem");
        var g = p.Group("计算机");
        g.Add("制造商", cs?.Str("Manufacturer"));
        g.Add("型号", cs?.Str("Model"));
        g.Add("系列", cs?.Str("SystemFamily"));
        g.Add("SKU", cs?.Str("SystemSKUNumber"));

        var board = One("Win32_BaseBoard");
        g = p.Group("主板");
        g.Add("制造商", board?.Str("Manufacturer"));
        g.Add("型号", board?.Str("Product"));
        g.Add("版本", board?.Str("Version"));
        g.Add("序列号", board?.Str("SerialNumber"));

        var bios = One("Win32_BIOS");
        g = p.Group("BIOS / 固件");
        g.Add("厂商", bios?.Str("Manufacturer"));
        g.Add("版本", bios?.Str("SMBIOSBIOSVersion"));
        g.Add("发布日期", Fmt.Date(bios?.Date("ReleaseDate")));
        if (bios?.U64("SMBIOSMajorVersion") is ulong major)
            g.Add("SMBIOS 版本", $"{major}.{bios.U64("SMBIOSMinorVersion") ?? 0}");
        g.Add("固件类型", FirmwareType());
        g.Add("安全启动", SecureBoot());

        var tpm = One("Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm");
        if (tpm != null)
        {
            g = p.Group("TPM");
            g.Add("规范版本", tpm.Str("SpecVersion")?.Split(',')[0]);
            g.Add("制造商", Fmt.Join(tpm.Str("ManufacturerIdTxt"), tpm.Str("ManufacturerVersion")));
            g.Add("状态", tpm.Bool("IsEnabled_InitialValue") == true ? "已启用" : "未启用");
        }
    }

    [DllImport("kernel32.dll")]
    static extern bool GetFirmwareType(out uint firmwareType);

    static string? FirmwareType()
    {
        try
        {
            return GetFirmwareType(out var t) ? t switch { 1 => "传统 BIOS (Legacy)", 2 => "UEFI", _ => null } : null;
        }
        catch { return null; }
    }

    static string? SecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return k?.GetValue("UEFISecureBootEnabled") is int v ? (v == 1 ? "已启用" : "未启用") : null;
    }

    // ───────────────────────────── 内存 ─────────────────────────────

    void MemoryPage(InfoPage p)
    {
        var os = One("Win32_OperatingSystem");
        var modules = Q("Win32_PhysicalMemory");
        var arrays = Q("Win32_PhysicalMemoryArray");

        var g = p.Group("物理内存");
        ulong installed = (ulong)modules.Sum(m => (decimal)(m.U64("Capacity") ?? 0));
        ulong visible = (os?.U64("TotalVisibleMemorySize") ?? 0) * 1024;
        ulong free = (os?.U64("FreePhysicalMemory") ?? 0) * 1024;
        if (installed > 0)
            g.Add("已安装", Fmt.Bytes(installed));
        if (visible > 0)
        {
            g.Add("系统可用", Fmt.Bytes(visible));
            g.Add("已使用", $"{Fmt.Bytes(visible - free)}（{(visible - free) * 100.0 / visible:0}%）");
            g.Add("空闲", Fmt.Bytes(free));
        }
        long slots = arrays.Sum(a => (long)(a.U64("MemoryDevices") ?? 0));
        if (slots > 0)
            g.Add("插槽", $"已用 {modules.Count} / 共 {slots}");
        decimal maxKb = arrays.Sum(a => (decimal)(a.U64("MaxCapacityEx") ?? a.U64("MaxCapacity") ?? 0));
        if (maxKb > 0)
            g.Add("最大支持", Fmt.Bytes((double)maxKb * 1024));

        if (Timings() is { } t)
        {
            g = p.Group("当前时序（读自内存控制器）");
            g.Add("运行频率", $"{t.MemClockMHz:0} MHz（DDR4-{t.DataRate}）");
            g.Add("主时序", $"{t.CL}-{t.RCDRD}-{t.RP}-{t.RAS}（CL-tRCD-tRP-tRAS）");
            g.Add("CAS 延迟（CL）", $"{t.CL} 时钟（{t.Ns(t.CL):0.00} ns）");
            g.Add("行到列延迟（tRCD）", t.RCDRD == t.RCDWR ? $"{t.RCDRD} 时钟" : $"读 {t.RCDRD} / 写 {t.RCDWR} 时钟");
            g.Add("行预充电（tRP）", $"{t.RP} 时钟");
            g.Add("行激活时间（tRAS）", $"{t.RAS} 时钟");
            g.Add("行周期（tRC）", $"{t.RC} 时钟");
            g.Add("指令比率（CR）", t.Cmd2T ? "2T" : "1T");
            g.Add("Gear Down 模式", t.GearDown ? "已启用" : "未启用");
            g.Add("次时序", $"tRRDS {t.RRDS} / tRRDL {t.RRDL} / tFAW {t.FAW} / tWTRS {t.WTRS} / tWTRL {t.WTRL} / tWR {t.WR} / tRTP {t.RTP} / tCWL {t.CWL}");
            g.Add("刷新周期（tRFC）", $"tRFC1 {t.RFC1}（{t.Ns(t.RFC1):0} ns）/ tRFC2 {t.RFC2}（{t.Ns(t.RFC2):0} ns）/ tRFC4 {t.RFC4}（{t.Ns(t.RFC4):0} ns）");
        }

        foreach (var m in modules)
        {
            ulong cap = m.U64("Capacity") ?? 0;
            g = p.Group(Fmt.Join(m.Str("DeviceLocator"), "-", Fmt.Bytes(cap), MemType(m)));
            g.Add("容量", Fmt.Bytes(cap));
            g.Add("类型", MemType(m));
            g.Add("外形", m.U64("FormFactor") switch { 8 => "DIMM", 12 => "SO-DIMM", _ => null });
            g.Add("标称速率", m.U64("Speed") is > 0 and var s ? $"{s} MT/s" : null);
            g.Add("运行速率", m.U64("ConfiguredClockSpeed") is > 0 and var c ? $"{c} MT/s" : null);
            g.Add("电压", m.U64("ConfiguredVoltage") is > 0 and var mv ? $"{mv / 1000.0:0.000} V" : null);
            g.Add("位宽", m.U64("DataWidth") is > 0 and var w ? $"{w} 位" : null);
            g.Add("制造商", m.Str("Manufacturer"));
            g.Add("型号", m.Str("PartNumber"));
            g.Add("序列号", m.Str("SerialNumber"));
            g.Add("插槽", Fmt.Join(m.Str("BankLabel"), m.Str("DeviceLocator")));
        }

        g = p.Group("虚拟内存");
        ulong vTotal = (os?.U64("TotalVirtualMemorySize") ?? 0) * 1024;
        ulong vFree = (os?.U64("FreeVirtualMemory") ?? 0) * 1024;
        if (vTotal > 0)
        {
            g.Add("提交上限", Fmt.Bytes(vTotal));
            g.Add("已提交", Fmt.Bytes(vTotal - vFree));
        }
        foreach (var pf in Q("Win32_PageFileUsage"))
            g.Add("页面文件 " + pf.Str("Name"), $"大小 {pf.U64("AllocatedBaseSize")} MB，当前 {pf.U64("CurrentUsage")} MB，峰值 {pf.U64("PeakUsage")} MB");
    }

    /// <summary>当前实际时序（只在 AMD 锐龙 + DDR4 上可读，见 <see cref="MemoryTimings"/>），一次收集只读一遍。</summary>
    MemoryTimingInfo? Timings()
    {
        if (!_timingsRead)
        {
            _timingsRead = true;
            bool ddr4 = Q("Win32_PhysicalMemory").Any(m => MemType(m) == "DDR4");
            _timings = MemoryTimings.TryRead(ddr4);
        }
        return _timings;
    }

    static string? MemType(ManagementBaseObject m)
    {
        string? t = m.U64("SMBIOSMemoryType") switch
        {
            20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5",
            27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5",
            _ => null,
        };
        return t ?? m.U64("MemoryType") switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", _ => null };
    }

    string MemorySummary()
    {
        var modules = Q("Win32_PhysicalMemory");
        ulong total = (ulong)modules.Sum(m => (decimal)(m.U64("Capacity") ?? 0));
        if (total == 0)
            total = (One("Win32_OperatingSystem")?.U64("TotalVisibleMemorySize") ?? 0) * 1024;
        var first = modules.FirstOrDefault();
        var speed = first?.U64("ConfiguredClockSpeed") is > 0 and var c ? c : first?.U64("Speed");
        var type = first == null ? null : MemType(first);
        var detail = type == null ? null : speed is > 0 ? $"{type}-{speed}" : type;
        long slots = Q("Win32_PhysicalMemoryArray").Sum(a => (long)(a.U64("MemoryDevices") ?? 0));
        var parts = new List<string>();
        if (detail != null) parts.Add(detail);
        if (slots > 0) parts.Add($"{modules.Count}/{slots} 插槽");
        if (Timings() is { } t) parts.Add($"{t.CL}-{t.RCDRD}-{t.RP}-{t.RAS}");
        return parts.Count == 0 ? Fmt.Bytes(total) : $"{Fmt.Bytes(total)}（{string.Join("，", parts)}）";
    }

    // ───────────────────────────── 显示设备 ─────────────────────────────

    void DisplayPage(InfoPage p)
    {
        foreach (var gpu in Q("Win32_VideoController"))
        {
            var g = p.Group(gpu.Str("Name") ?? "显示适配器");
            g.Add("名称", gpu.Str("Name"));
            ulong vram = Vram(gpu);
            if (vram > 0) g.Add("显存", Fmt.Bytes(vram));
            g.Add("GPU", gpu.Str("VideoProcessor"));
            g.Add("驱动版本", gpu.Str("DriverVersion"));
            g.Add("驱动日期", Fmt.Date(gpu.Date("DriverDate")));
            if (gpu.U64("CurrentHorizontalResolution") is > 0 and var w)
                g.Add("当前模式", $"{w} × {gpu.U64("CurrentVerticalResolution")}，{gpu.U64("CurrentRefreshRate")} Hz，{gpu.U64("CurrentBitsPerPixel")} 位色");
            g.Add("设备 ID", gpu.Str("PNPDeviceID"));
        }

        foreach (var m in Monitors())
        {
            var g = p.Group("显示器：" + m.Name);
            g.Add("名称", m.Name);
            g.Add("制造商代码", m.Manufacturer);
            if (m.DiagonalInch is double d) g.Add("尺寸", $"{d:0.#} 英寸");
            if (m.Year is int y) g.Add("生产年份", y.ToString());
            g.Add("序列号", m.Serial);
        }

        var screens = DisplayMonitors();
        if (screens.Count > 0)
        {
            var g = p.Group("桌面");
            foreach (var s in screens)
                g.Add(s.Device.TrimStart('\\', '.'), $"{s.Width} × {s.Height}，位置 ({s.X}, {s.Y}){(s.Primary ? "，主显示器" : "")}");
        }
    }

    // 用 Win32 API 枚举显示器，避免依赖 WinForms 的 Screen（WPF 版也要用这份代码）
    sealed record ScreenInfo(string Device, int X, int Y, int Width, int Height, bool Primary);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfoEx
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect32 rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    static List<ScreenInfo> DisplayMonitors()
    {
        var list = new List<ScreenInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr _, ref Rect32 _, IntPtr _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), Device = "" };
                if (GetMonitorInfo(mon, ref info))
                {
                    var r = info.Monitor;
                    list.Add(new ScreenInfo(info.Device, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.Flags & 1) != 0));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    /// <summary>Win32_VideoController.AdapterRAM 是 32 位，超过 4GB 会溢出，所以优先读驱动注册表。</summary>
    ulong Vram(ManagementBaseObject gpu)
    {
        _vram ??= ReadVramFromRegistry();
        var name = gpu.Str("Name");
        if (name != null && _vram.TryGetValue(name, out var v))
            return v;
        return gpu.U64("AdapterRAM") ?? 0;
    }

    static Dictionary<string, ulong> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null)
                return result;
            foreach (var sub in cls.GetSubKeyNames().Where(s => s.All(char.IsDigit)))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is not string desc)
                        continue;
                    ulong size = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => (ulong)l,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b),
                        _ => 0UL,
                    };
                    if (size == 0)
                    {
                        size = k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => (ulong)(uint)i,
                            long l => (ulong)l,
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b),
                            _ => 0UL,
                        };
                    }
                    if (size > 0 && (!result.TryGetValue(desc, out var old) || size > old))
                        result[desc] = size;
                }
                catch
                {
                    // 个别子键没有读取权限，跳过
                }
            }
        }
        catch { }
        return result;
    }

    List<MonitorInfo> Monitors()
    {
        if (_monitors != null)
            return _monitors;

        var sizes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Q("WmiMonitorBasicDisplayParams", @"root\wmi"))
        {
            double h = o.U64("MaxHorizontalImageSize") ?? 0, v = o.U64("MaxVerticalImageSize") ?? 0;
            if (h > 0 && v > 0 && o.Str("InstanceName") is string inst)
                sizes[inst] = Math.Sqrt(h * h + v * v) / 2.54;
        }

        var list = new List<MonitorInfo>();
        foreach (var o in Q("WmiMonitorID", @"root\wmi"))
        {
            var inst = o.Str("InstanceName") ?? "";
            var name = Edid(o, "UserFriendlyName") ?? "通用显示器";
            int? year = o.U64("YearOfManufacture") is > 0 and var y ? (int)y : null;
            list.Add(new MonitorInfo(name, Edid(o, "ManufacturerName"), Edid(o, "SerialNumberID"), year,
                sizes.TryGetValue(inst, out var d) ? d : null));
        }
        return _monitors = list;
    }

    /// <summary>EDID 字段在 WMI 里是以 0 结尾的 UInt16 数组。</summary>
    static string? Edid(ManagementBaseObject o, string prop)
    {
        if (o.Raw(prop) is not ushort[] arr)
            return null;
        var s = new string(arr.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
        return s.Length == 0 || s == "0" ? null : s;
    }

    // ───────────────────────────── 存储 ─────────────────────────────

    void StoragePage(InfoPage p)
    {
        foreach (var d in Disks())
        {
            var g = p.Group(d.Model);
            g.Add("型号", d.Model);
            g.Add("容量", d.Size > 0 ? $"{Fmt.Bytes(d.Size)}（{d.Size / 1e9:0.#} GB 十进制）" : null);
            g.Add("接口", d.Bus);
            g.Add("介质类型", d.Media);
            g.Add("序列号", d.Serial);
            g.Add("固件版本", d.Firmware);
            g.Add("健康状态", d.Health);
        }

        var vols = Volumes();
        if (vols.Count > 0)
        {
            var g = p.Group("分区");
            foreach (var v in vols)
            {
                long used = v.TotalSize - v.TotalFreeSpace;
                var label = string.IsNullOrEmpty(v.VolumeLabel) ? "" : $"（{v.VolumeLabel}）";
                g.Add(v.Name.TrimEnd('\\') + label,
                    $"{v.DriveFormat}，{DriveTypeName(v.DriveType)}，总 {Fmt.Bytes(v.TotalSize)}，已用 {Fmt.Bytes(used)}（{used * 100.0 / Math.Max(1, v.TotalSize):0}%），可用 {Fmt.Bytes(v.AvailableFreeSpace)}");
            }
        }
    }

    List<DiskInfo> Disks()
    {
        if (_disks != null)
            return _disks;

        var list = new List<DiskInfo>();
        var physical = Q("MSFT_PhysicalDisk", StorageScope)
            .OrderBy(d => int.TryParse(d.Str("DeviceId"), out var n) ? n : int.MaxValue);
        foreach (var d in physical)
        {
            list.Add(new DiskInfo(d.Str("FriendlyName") ?? "磁盘", d.U64("Size") ?? 0,
                BusType(d.U64("BusType")), MediaType(d.U64("MediaType")),
                d.Str("SerialNumber"), d.Str("FirmwareVersion"), Health(d.U64("HealthStatus"))));
        }

        // 存储管理 API 不可用时退回旧的 Win32_DiskDrive
        if (list.Count == 0)
        {
            foreach (var d in Q("Win32_DiskDrive").OrderBy(d => d.U64("Index") ?? 0))
            {
                list.Add(new DiskInfo(d.Str("Model") ?? "磁盘", d.U64("Size") ?? 0, d.Str("InterfaceType"),
                    d.Str("MediaType"), d.Str("SerialNumber"), d.Str("FirmwareRevision"), d.Str("Status")));
            }
        }
        return _disks = list;
    }

    static List<DriveInfo> Volumes()
    {
        try { return DriveInfo.GetDrives().Where(d => d.IsReady).ToList(); }
        catch { return []; }
    }

    static string? BusType(ulong? t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "虚拟磁盘", 16 => "存储空间", 17 => "NVMe",
        _ => null,
    };

    static string? MediaType(ulong? t) => t switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };

    static string? Health(ulong? t) => t switch { 0 => "健康", 1 => "警告", 2 => "不健康", _ => null };

    static string DriveTypeName(DriveType t) => t switch
    {
        DriveType.Fixed => "本地磁盘",
        DriveType.Removable => "可移动磁盘",
        DriveType.Network => "网络驱动器",
        DriveType.CDRom => "光驱",
        DriveType.Ram => "内存盘",
        _ => "其他",
    };

    // ───────────────────────────── 网络 ─────────────────────────────

    void NetworkPage(InfoPage p)
    {
        var real = RealAdapters();
        foreach (var n in Nics())
        {
            var g = p.Group(n.Name);
            bool up = n.OperationalStatus == OperationalStatus.Up;
            g.Add("描述", n.Description);
            g.Add("类型", NicType(n.NetworkInterfaceType));
            if (real.TryGetValue(n.Name, out var hardware))
                g.Add("类别", hardware ? "物理网卡" : "虚拟网卡");
            g.Add("状态", up ? "已连接" : "未连接");
            if (up && n.Speed > 0)
                g.Add("连接速率", Fmt.BitRate(n.Speed));
            var mac = n.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length > 0)
                g.Add("MAC 地址", string.Join("-", mac.Select(b => b.ToString("X2"))));

            var ip = n.GetIPProperties();
            g.Add("IPv4 地址", Ips(n, AddressFamily.InterNetwork));
            g.Add("IPv6 地址", Ips(n, AddressFamily.InterNetworkV6));
            g.Add("默认网关", string.Join(", ", ip.GatewayAddresses.Select(a => a.Address.ToString())));
            g.Add("DNS 服务器", string.Join(", ", ip.DnsAddresses.Select(a => a.ToString())));
            try
            {
                g.Add("DHCP", ip.GetIPv4Properties()?.IsDhcpEnabled switch { true => "已启用", false => "未启用", null => null });
            }
            catch (NetworkInformationException)
            {
                // 该接口没有 IPv4
            }
        }
    }

    /// <summary>真正的网卡（见 <see cref="NetAdapters"/>），一次收集内只查一遍 WMI。</summary>
    Dictionary<string, bool> RealAdapters() => _adapters ??= NetAdapters.Query();

    List<NetworkInterface> Nics()
    {
        try
        {
            var real = RealAdapters();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Where(n => NetAdapters.IsReal(n.Name, real))
                .OrderBy(n => n.OperationalStatus == OperationalStatus.Up ? 0 : 1)
                .ThenBy(n => real.TryGetValue(n.Name, out var hw) && hw ? 0 : 1)
                .ToList();
        }
        catch { return []; }
    }

    static string NicType(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "以太网",
        NetworkInterfaceType.Wireless80211 => "无线局域网",
        NetworkInterfaceType.Ppp => "PPP 拨号",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "移动宽带",
        _ when !Enum.IsDefined(t) => "虚拟",
        _ => t.ToString(),
    };

    static string Ips(NetworkInterface n, AddressFamily family)
    {
        try
        {
            return string.Join(", ", n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == family)
                .Select(a => family == AddressFamily.InterNetwork ? $"{a.Address}/{a.PrefixLength}" : a.Address.ToString()));
        }
        catch { return ""; }
    }
}
