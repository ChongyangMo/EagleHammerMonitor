using System.Text.RegularExpressions;

namespace EagleHammerMonitor;

/// <summary>
/// 判断一个网络接口是不是“真正的网卡”。
/// .NET 的 GetAllNetworkInterfaces（LHM 的网络传感器也基于它）会把 NDIS 过滤驱动（QoS、WFP 等 “-0000” 结尾）、
/// 隐藏的虚拟接口（“本地连接* N”）、已拔掉的旧网卡都当成接口返回。
/// 以 MSFT_NetAdapter（即 Get-NetAdapter 的数据源）为准筛选。
/// </summary>
static partial class NetAdapters
{
    static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(60);

    static Dictionary<string, bool>? _cache;
    static DateTime _cacheTime;

    /// <summary>当前真正的网卡。键为接口名（如“以太网”“WLAN 4”），值为是否物理网卡。</summary>
    public static Dictionary<string, bool> Query()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Wmi.Query("SELECT Name, HardwareInterface FROM MSFT_NetAdapter", @"root\StandardCimv2"))
            if (a.Str("Name") is string name)
                map[name] = a.Bool("HardwareInterface") == true;
        return map;
    }

    /// <summary>
    /// 带缓存的判断，供每秒刷新的传感器快照使用（网卡增减如插拔 USB 网卡、连 VPN 最多延迟一分钟反映）。
    /// 只在传感器线程上调用。
    /// </summary>
    public static bool IsReal(string name)
    {
        if (_cache == null || DateTime.UtcNow - _cacheTime > CacheTime)
        {
            _cache = Query();
            _cacheTime = DateTime.UtcNow;
        }
        return IsReal(name, _cache);
    }

    /// <summary>WMI 查询失败（列表为空）时退回到只排除过滤驱动。</summary>
    public static bool IsReal(string name, Dictionary<string, bool> real) =>
        real.Count > 0 ? real.ContainsKey(name) : !FilterSuffix().IsMatch(name);

    [GeneratedRegex(@"-\d{4}$")]
    private static partial Regex FilterSuffix();
}
