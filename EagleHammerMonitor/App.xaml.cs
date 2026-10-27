using System.Windows;

namespace EagleHammerMonitor;

public partial class App : Application
{
    internal static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // EagleHammerMonitor.exe --report <文件>：不打开界面，直接生成文本报告
        if (e.Args.Length >= 2 && e.Args[0].Equals("--report", StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(Report.RunHeadless(e.Args[1]));
            return;
        }

        System.Windows.Forms.Application.EnableVisualStyles(); // 托盘右键菜单用
        Settings = AppSettings.Load();
        ThemeMode = Settings.Theme.ToWpf();
        new MainWindow().Show();
    }
}
