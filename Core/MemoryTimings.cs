using LibreHardwareMonitor.PawnIo;
using X86 = System.Runtime.Intrinsics.X86;

namespace SysMonitor;

/// <summary>内存当前实际运行的时序（BIOS / XMP 设置后写进内存控制器的值，单位：时钟周期）。</summary>
public sealed record MemoryTimingInfo(
    double MemClockMHz,
    uint CL, uint RCDRD, uint RCDWR, uint RP, uint RAS, uint RC,
    bool Cmd2T, bool GearDown,
    uint RRDS, uint RRDL, uint FAW, uint WTRS, uint WTRL, uint WR, uint RTP, uint CWL,
    uint RFC1, uint RFC2, uint RFC4)
{
    public int DataRate => (int)Math.Round(MemClockMHz * 2);

    /// <summary>时钟周期数换算成纳秒。</summary>
    public double Ns(uint cycles) => cycles * 1000.0 / MemClockMHz;
}

/// <summary>
/// 读取 AMD 锐龙内存控制器（UMC）里的实际时序，和 CPU-Z、ZenTimings 的做法一样：
/// 通过 PawnIO 驱动的 AMDFamily17 模块读 SMN 总线上的 UMC 寄存器（只读，不写任何东西）。
///
/// LibreHardwareMonitor 只读内存条 SPD 里的出厂默认值（纳秒），不读这里，所以要单独实现。
/// 寄存器布局只在 Zen 2/3 + DDR4 上与 CPU-Z 逐项核对过（5950X：18-22-22-42、tRC 69、1T、1500 MHz 全部一致），
/// 其他平台（Intel、Zen 4/5 的 DDR5）布局不同，直接不读，避免显示错误数字。
/// </summary>
public static class MemoryTimings
{
    // 每个内存通道的 UMC 基址；时序寄存器在基址 + 0x200 起
    static readonly uint[] ChannelBases = [0x50000, 0x150000];

    public static MemoryTimingInfo? TryRead(bool isDdr4)
    {
        if (!isDdr4 || !IsSupportedAmdCpu() || !PawnIo.IsInstalled)
            return null;

        AmdFamily17? smn = null;
        try
        {
            smn = new AmdFamily17();
            foreach (uint channel in ChannelBases)
            {
                var info = ReadChannel(smn, channel);
                if (info != null)
                    return info; // 各通道一般相同，取第一个有效的
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            smn?.Close();
        }
    }

    static MemoryTimingInfo? ReadChannel(AmdFamily17 smn, uint channel)
    {
        uint R(uint offset) => smn.ReadSmn(channel + offset);
        static uint Bits(uint v, int hi, int lo) => (v >> lo) & ((1u << (hi - lo + 1)) - 1);

        uint cfg = R(0x200);
        uint ratio = Bits(cfg, 6, 0); // MEMCLK = 倍率 × 100/3 MHz
        uint t204 = R(0x204), t208 = R(0x208), t20C = R(0x20C), t210 = R(0x210), t214 = R(0x214), t218 = R(0x218), t260 = R(0x260);
        uint cl = Bits(t204, 5, 0);

        // 驱动没装好时读回全 0；空通道也是 0。做个合理性检查
        if (ratio == 0 || cl is < 5 or > 60)
            return null;

        return new MemoryTimingInfo(
            MemClockMHz: ratio * 100.0 / 3,
            CL: cl, RCDRD: Bits(t204, 21, 16), RCDWR: Bits(t204, 29, 24), RP: Bits(t208, 21, 16), RAS: Bits(t204, 14, 8), RC: Bits(t208, 7, 0),
            Cmd2T: Bits(cfg, 10, 10) == 1, GearDown: Bits(cfg, 11, 11) == 1,
            RRDS: Bits(t20C, 4, 0), RRDL: Bits(t20C, 12, 8), FAW: Bits(t210, 7, 0),
            WTRS: Bits(t214, 12, 8), WTRL: Bits(t214, 22, 16), WR: Bits(t218, 7, 0), RTP: Bits(t20C, 28, 24), CWL: Bits(t214, 5, 0),
            RFC1: Bits(t260, 10, 0), RFC2: Bits(t260, 21, 11), RFC4: Bits(t260, 31, 22));
    }

    /// <summary>AMD Family 17h（Zen/Zen+/Zen 2）或 19h（Zen 3/4）。DDR4 的限制另外判断。</summary>
    static bool IsSupportedAmdCpu()
    {
        if (!X86.X86Base.IsSupported)
            return false;
        var (_, ebx, ecx, edx) = X86.X86Base.CpuId(0, 0);
        bool amd = ebx == 0x68747541 && edx == 0x69746E65 && ecx == 0x444D4163; // "AuthenticAMD"
        if (!amd)
            return false;
        int eax = X86.X86Base.CpuId(1, 0).Eax;
        int family = (eax >> 8) & 0xF;
        if (family == 0xF)
            family += (eax >> 20) & 0xFF;
        return family is 0x17 or 0x19;
    }
}
