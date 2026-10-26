using System.Diagnostics;
using System.Windows;

namespace SysMonitor;

public partial class MainWindow
{
    void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource))
            RefreshStartupMenu();
    }

    void RefreshStartupMenu()
    {
        try
        {
            StartupItem.IsChecked = StartupService.IsEnabled();
            StartupItem.IsEnabled = true;
            StartupItem.ToolTip = null;
        }
        catch (Exception ex)
        {
            StartupItem.IsEnabled = false;
            StartupItem.ToolTip = "无法读取开机启动设置：" + ex.Message;
            Trace.TraceError("读取开机启动设置失败：{0}", ex);
        }
    }

    void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupService.SetEnabled(StartupItem.IsChecked);
        }
        catch (Exception ex)
        {
            Trace.TraceError("设置开机启动失败：{0}", ex);
            MessageBox.Show(this, "设置开机启动失败：" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshStartupMenu();
        }
    }
}
