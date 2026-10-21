using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace SysMonitor;

/// <summary>托盘图标：图标上直接画 CPU 温度，按温度变色；提示文字显示 CPU/内存/GPU 摘要。</summary>
sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    Icon? _current;
    IntPtr _handle;
    string? _text;
    Color _color;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => show());
        menu.Items.Add("退出", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { Text = "SysMonitor", Icon = AppIcon(), ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>传感器出数之前先显示程序图标（exe 内嵌的那个）。</summary>
    static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    public void Update(SensorSnapshot s, List<string> stats)
    {
        float? temp = SensorPicks.CpuTemp(s)?.Value;
        float? load = SensorPicks.CpuLoad(s)?.Value;
        string text = temp is float t ? Math.Round(t).ToString("0") : load is float l ? Math.Round(l).ToString("0") : "--";
        Color color = temp is float c
            ? c >= 85 ? Color.FromArgb(255, 90, 90) : c >= 70 ? Color.FromArgb(255, 175, 60) : Color.FromArgb(120, 230, 140)
            : Color.White;

        if (text != _text || color != _color)
        {
            _text = text;
            _color = color;
            SetIcon(text, color);
        }

        var tip = stats.Count > 0 ? string.Join("\n", stats) : "SysMonitor";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void SetIcon(string text, Color color)
    {
        var size = Forms.SystemInformation.SmallIconSize;
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(235, 24, 24, 28));
            g.FillRectangle(bg, 0, 0, size.Width, size.Height);
            float px = size.Height * (text.Length >= 3 ? 0.6f : 0.8f);
            using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(color);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, fg, new RectangleF(-2, 0, size.Width + 4, size.Height + 1), sf);
        }

        IntPtr handle = bmp.GetHicon();
        var oldIcon = _current;
        var oldHandle = _handle;
        _current = Icon.FromHandle(handle);
        _handle = handle;
        _icon.Icon = _current;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero)
            DestroyIcon(oldHandle);
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _current?.Dispose();
        if (_handle != IntPtr.Zero)
            DestroyIcon(_handle);
    }
}
