using System.Diagnostics;
using System.Windows;

namespace EagleHammerMonitor;

public partial class App : Application
{
    /// <summary>
    /// 整台电脑只运行一份监控：两份会在任务栏同一位置各画一个面板，数字叠在一起。
    /// 新启动的取代旧的：先通知旧的正常退出（托盘图标、任务栏面板都能收干净），等不到再强制结束。
    /// </summary>
    const string InstanceName = @"Global\EagleHammerMonitor";

    /// <summary>要结束的进程名：现在的程序，加上改名前的 SysMonitor（它没有退出通知，只能强制结束）。</summary>
    static readonly string[] ProcessNames = ["EagleHammerMonitor", "SysMonitor"];

    internal static AppSettings Settings { get; private set; } = new();

    // 进程存活期间一直持有，退出时由系统释放
    static Mutex? _instance;
    static EventWaitHandle? _exitSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // EagleHammerMonitor.exe --report <文件>：不打开界面，直接生成文本报告（不影响正在运行的监控）
        if (e.Args.Length >= 2 && e.Args[0].Equals("--report", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(Report.RunHeadless(e.Args[1]));
            return;
        }

        TakeOver();

        System.Windows.Forms.Application.EnableVisualStyles(); // 托盘右键菜单用
        Settings = AppSettings.Load();
        ThemeMode = Settings.Theme.ToWpf();
        var window = new MainWindow();
        window.Show();
        // 之后再有新的启动，轮到自己被取代
        ThreadPool.RegisterWaitForSingleObject(_exitSignal!,
            (_, _) => window.Dispatcher.BeginInvoke(window.ExitApp), null, Timeout.Infinite, true);
    }

    /// <summary>结束其他正在运行的监控，拿到“唯一实例”的身份。</summary>
    static void TakeOver()
    {
        _instance = new Mutex(false, InstanceName);
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Exit", out bool createdSignal);

        bool acquired;
        try { acquired = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; } // 上一份被强制结束过
        if (!acquired)
        {
            // 有一份在运行：请它退出，最多等 5 秒
            if (!createdSignal)
                _exitSignal.Set();
            try { acquired = _instance.WaitOne(5000); }
            catch (AbandonedMutexException) { acquired = true; }
        }

        // 没响应的、旧版 SysMonitor.exe（没有退出通知）：强制结束
        int self = Environment.ProcessId;
        foreach (var name in ProcessNames)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                {
                    if (p.Id == self)
                        continue;
                    try
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch (Exception ex) { Trace.TraceError("结束旧的监控进程 {0} 失败：{1}", p.Id, ex.Message); }
                }

        if (!acquired)
        {
            try { _instance.WaitOne(3000); }
            catch (AbandonedMutexException) { }
        }
        _exitSignal.Reset(); // 刚才发给旧进程的通知如果没被取走，别让自己收到
    }
}
