using System.Collections.Concurrent;
using LibreHardwareMonitor.Hardware;

namespace SysMonitor;

public sealed record SensorReading(
    string Id,
    string HardwareId,
    string HardwareName,
    HardwareType HardwareType,
    SensorType Type,
    string Name,
    float? Value,
    float? Min,
    float? Max)
{
    /// <summary>中文显示名（<see cref="Name"/> 保留英文原名，供内部匹配和搜索）。</summary>
    public string DisplayName => SensorNames.Translate(Name);

    /// <summary>中文硬件名，比如 “Total Memory” → “物理内存”；具体型号不翻译。</summary>
    public string HardwareDisplayName { get; init; } = HardwareName;
}

/// <summary>某一时刻所有传感器读数的只读快照，可安全地跨线程传递。</summary>
public sealed class SensorSnapshot
{
    public DateTime Time { get; }
    public IReadOnlyList<SensorReading> Readings { get; }
    public IReadOnlyDictionary<string, SensorReading> ById { get; }

    SensorSnapshot(List<SensorReading> readings)
    {
        Time = DateTime.Now;
        Readings = readings;
        var byId = new Dictionary<string, SensorReading>(readings.Count);
        foreach (var r in readings)
            byId.TryAdd(r.Id, r);
        ById = byId;
    }

    internal static SensorSnapshot Build(IComputer computer)
    {
        var list = new List<SensorReading>();

        void Add(IHardware hw, IHardware? parent)
        {
            // LHM 把过滤驱动、隐藏虚拟接口、已拔掉的旧网卡都当成网卡，数据与真实网卡重复，跳过
            if (hw.HardwareType == HardwareType.Network && !NetAdapters.IsReal(hw.Name))
                return;

            string name = parent == null ? hw.Name : $"{parent.Name} › {hw.Name}";
            string displayName = parent == null
                ? SensorNames.TranslateHardware(hw.Name, hw.HardwareType)
                : $"{SensorNames.TranslateHardware(parent.Name, parent.HardwareType)} › {SensorNames.TranslateHardware(hw.Name, hw.HardwareType)}";
            string hwId = hw.Identifier.ToString();
            foreach (var s in hw.Sensors.OrderBy(s => SensorFormat.Order(s.SensorType)).ThenBy(s => s.Index))
                list.Add(new SensorReading(s.Identifier.ToString(), hwId, name, hw.HardwareType, s.SensorType, s.Name, s.Value, s.Min, s.Max)
                {
                    HardwareDisplayName = displayName,
                });
            foreach (var sub in hw.SubHardware)
                Add(sub, hw);
        }

        foreach (var hw in computer.Hardware)
            Add(hw, null);
        return new SensorSnapshot(list);
    }
}

/// <summary>
/// 在专用后台线程上驱动 LibreHardwareMonitor：LHM 的对象不是线程安全的，
/// 所有对它的访问（打开、刷新、重置最值、关闭）都放在这一个线程里。
/// </summary>
public sealed class SensorService : IDisposable
{
    readonly AutoResetEvent _wake = new(false);
    readonly ConcurrentQueue<Action<IComputer>> _commands = new();
    Thread? _thread;
    volatile bool _stop;
    volatile int _intervalMs = 1000;

    /// <summary>每次刷新完成后在后台线程触发。</summary>
    public event Action<SensorSnapshot>? Updated;

    public event Action<string>? Failed;

    public int IntervalMs
    {
        get => _intervalMs;
        set
        {
            _intervalMs = Math.Max(250, value);
            _wake.Set();
        }
    }

    public void Start()
    {
        if (_thread != null)
            return;
        _thread = new Thread(Run) { IsBackground = true, Name = "SensorService" };
        _thread.Start();
    }

    public void ResetMinMax() => Post(c =>
    {
        foreach (var s in AllSensors(c))
        {
            s.ResetMin();
            s.ResetMax();
        }
    });

    void Post(Action<IComputer> command)
    {
        _commands.Enqueue(command);
        _wake.Set();
    }

    void Run()
    {
        var computer = CreateComputer();
        try
        {
            computer.Open();
        }
        catch (Exception ex)
        {
            Failed?.Invoke("初始化失败：" + ex.Message);
            return;
        }

        try
        {
            while (!_stop)
            {
                while (_commands.TryDequeue(out var cmd))
                {
                    try { cmd(computer); }
                    catch { }
                }

                try
                {
                    UpdateAll(computer);
                    Updated?.Invoke(SensorSnapshot.Build(computer));
                }
                catch (Exception ex)
                {
                    Failed?.Invoke(ex.Message);
                }

                _wake.WaitOne(_intervalMs);
            }
        }
        finally
        {
            try { computer.Close(); }
            catch { }
        }
    }

    /// <summary>不开后台线程，读一次（两次刷新，让负载类传感器有值）后关闭。用于命令行导出报告。</summary>
    public static SensorSnapshot CaptureOnce()
    {
        var computer = CreateComputer();
        computer.Open();
        try
        {
            UpdateAll(computer);
            Thread.Sleep(1000);
            UpdateAll(computer);
            return SensorSnapshot.Build(computer);
        }
        finally
        {
            computer.Close();
        }
    }

    static Computer CreateComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsNetworkEnabled = true,
        IsStorageEnabled = true,
        IsPsuEnabled = true,
        IsBatteryEnabled = true,
        IsPowerMonitorEnabled = true,
    };

    static void UpdateAll(IComputer computer)
    {
        foreach (var hw in computer.Hardware)
            Update(hw);

        static void Update(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware)
                Update(sub);
        }
    }

    static IEnumerable<ISensor> AllSensors(IComputer computer)
    {
        static IEnumerable<ISensor> Walk(IHardware hw) => hw.Sensors.Concat(hw.SubHardware.SelectMany(Walk));
        return computer.Hardware.SelectMany(Walk);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(3000);
    }
}
