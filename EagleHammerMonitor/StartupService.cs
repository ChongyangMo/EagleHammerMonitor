using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace EagleHammerMonitor;

/// <summary>按当前用户保存登录启动任务，以管理员权限运行硬件采集。</summary>
static class StartupService
{
    const int TaskCreateOrUpdate = 6;
    const int TaskLogonInteractiveToken = 3;
    const int FileNotFound = unchecked((int)0x80070002);
    static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    static string UserId => WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("无法获取当前用户身份。");
    static string TaskName => "EagleHammerMonitor-Startup-" + UserId;

    /// <summary>改名前（SysMonitor）用的任务名，启动时迁移，见 <see cref="MigrateLegacyTask"/>。</summary>
    static string LegacyTaskName => "SysMonitor-Startup-" + UserId;

    /// <summary>程序文件名（随 csproj 的 AssemblyName）。</summary>
    static string ExeName => typeof(StartupService).Assembly.GetName().Name + ".exe";

    public static bool IsEnabled() => WithFolder<bool>(folder =>
    {
        object? task = null;
        try
        {
            task = folder.GetTask(TaskName);
            dynamic registered = task;
            var xml = XDocument.Parse((string)registered.Xml);
            var command = xml.Root?.Element(TaskNamespace + "Actions")?
                .Element(TaskNamespace + "Exec")?.Element(TaskNamespace + "Command")?.Value;
            return (bool)registered.Enabled && string.Equals(command, ExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex.HResult == FileNotFound)
        {
            return false;
        }
        finally { Release(task); }
    });

    public static void SetEnabled(bool enabled) => WithFolder<bool>(folder =>
    {
        if (!enabled)
        {
            try { folder.DeleteTask(TaskName, 0); }
            catch (Exception ex) when (ex.HResult == FileNotFound) { }
            return true;
        }

        string executable = ExecutablePath();
        string user = UserId;
        XElement Element(string name, params object[] content) => new(TaskNamespace + name, content);
        var xml = new XDocument(
            new XElement(TaskNamespace + "Task", new XAttribute("version", "1.2"),
                Element("RegistrationInfo", Element("Description", AppInfo.Name + " 开机启动")),
                Element("Triggers", Element("LogonTrigger", Element("Enabled", true), Element("UserId", user))),
                Element("Principals", new XElement(TaskNamespace + "Principal", new XAttribute("id", "CurrentUser"),
                    Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
                Element("Settings",
                    Element("MultipleInstancesPolicy", "IgnoreNew"),
                    Element("DisallowStartIfOnBatteries", false),
                    Element("StopIfGoingOnBatteries", false),
                    Element("Enabled", true),
                    Element("ExecutionTimeLimit", "PT0S")),
                new XElement(TaskNamespace + "Actions", new XAttribute("Context", "CurrentUser"),
                    Element("Exec", Element("Command", executable),
                        Element("WorkingDirectory", Path.GetDirectoryName(executable)!)))));
        object registered = folder.RegisterTask(TaskName, xml.ToString(), TaskCreateOrUpdate,
            user, null, TaskLogonInteractiveToken, null);
        Release(registered);
        return true;
    });

    /// <summary>
    /// 改名前注册的开机启动任务指向旧的 SysMonitor.exe：删掉它，原来开着的话改为启动现在这个程序。
    /// 不处理的话旧任务会在登录时继续启动旧程序，菜单上的“开机启动”也显示为未勾选。
    /// </summary>
    public static void MigrateLegacyTask()
    {
        bool wasEnabled = WithFolder<bool>(folder =>
        {
            object? task = null;
            try
            {
                task = folder.GetTask(LegacyTaskName);
                bool enabled = ((dynamic)task).Enabled;
                folder.DeleteTask(LegacyTaskName, 0);
                return enabled;
            }
            catch (Exception ex) when (ex.HResult == FileNotFound)
            {
                return false;
            }
            finally { Release(task); }
        });
        if (wasEnabled)
            SetEnabled(true);
    }

    static string ExecutablePath()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径。");
        if (!Path.GetFileName(path).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"请通过 {ExeName} 设置开机启动。");
        return path;
    }

    static T WithFolder<T>(Func<dynamic, T> action)
    {
        object? service = null, folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("系统任务计划程序不可用。");
            service = Activator.CreateInstance(type)!;
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder(@"\");
            return action(folder);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}
