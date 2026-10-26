using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SysMonitor;

/// <summary>标签的颜色：普通标签用淡色，上传/下载箭头各用一种颜色，一眼分得出方向。</summary>
enum LabelTone { Normal, Up, Down }

/// <summary>任务栏上的一行：标签 + 若干数值格。</summary>
sealed record BarRow(string Label, BarCell[] Cells, LabelTone Tone = LabelTone.Normal);

/// <summary>
/// 一个数值格：粗体数字 + 淡色单位。Template/UnitTemplate 是数字和单位最宽时的样子，用来固定列宽，数字变化时不跳动；
/// Level 1 偏高（橙）、2 过高（红）；Fill（0~1）不为空时在数字前画一根占用率小条。
/// </summary>
readonly record struct BarCell(string Value, string Unit, string Template, string UnitTemplate, int Level = 0, float? Fill = null);

/// <summary>
/// 在 Win11 任务栏的空白处显示数据，三块（CPU+内存、GPU+显存、上传+下载）并排放在用户选的一侧：
/// 左侧 = 居中图标组左边的空白（靠左），右侧 = 图标组和托盘之间的空白（贴着托盘）。
/// 选了左侧但左边没空间（任务栏靠左对齐）时改放右侧；空间不够时从最后一块（网速）开始去掉。
/// 做法：在任务栏窗口（Shell_TrayWnd）下建逐像素透明的子窗口，随任务栏移动、全屏时随任务栏一起隐藏。
/// 居中图标组的范围用 UI 自动化读取（图标数量会变），每秒查一次。
/// 子窗口跑在独立线程上：跨进程父子窗口会把双方的输入队列连在一起，主界面卡住时不能拖累任务栏。
/// </summary>
sealed class TaskbarPanel : IDisposable
{
    public static bool Supported => Environment.OSVersion.Version.Build >= 22000;

    readonly Action _show, _exit;
    readonly Action<TaskbarSide?> _setSide;
    readonly Thread _thread;
    readonly System.Threading.Timer _layoutTimer;
    readonly PanelWindow _panel;
    Forms.Control _invoker = null!;
    Forms.ContextMenuStrip _menu = null!;
    Forms.ToolStripItem _moveItem = null!;
    volatile bool _disposed;

    // 以下只在窗口线程上访问
    Layout? _layout;
    List<BarRow>[] _rows = [];
    bool _enabled;
    TaskbarSide _side;
    BarStyle? _style;

    // 以下只在布局计时器里访问（_layoutBusy 保证同一时刻只有一个回调在跑）
    int _layoutBusy;
    AutomationElement? _frame;
    IntPtr _frameOwner;

    /// <param name="show">点击时打开主窗口</param>
    /// <param name="exit">右键菜单“退出”</param>
    /// <param name="setSide">右键菜单“移到左侧/右侧”（传入新位置）、“隐藏任务栏数据”（传入 null）</param>
    public TaskbarPanel(Action show, Action exit, Action<TaskbarSide?> setSide)
    {
        _show = show;
        _exit = exit;
        _setSide = setSide;
        _panel = new PanelWindow(this);
        _layoutTimer = new System.Threading.Timer(_ => PollLayout(), null, Timeout.Infinite, Timeout.Infinite);

        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _invoker = new Forms.Control();
            _ = _invoker.Handle; // 先建好句柄，别的线程才能 BeginInvoke
            _menu = new Forms.ContextMenuStrip();
            _menu.Items.Add("打开 SysMonitor", null, (_, _) => _show());
            _moveItem = _menu.Items.Add("", null, (_, _) => _setSide(_side == TaskbarSide.Left ? TaskbarSide.Right : TaskbarSide.Left));
            _menu.Items.Add("隐藏任务栏数据", null, (_, _) => _setSide(null));
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add("退出", null, (_, _) => _exit());
            ready.Set();
            Forms.Application.Run();
        })
        { IsBackground = true, Name = "TaskbarPanel" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    public void Configure(bool enabled, TaskbarSide side)
    {
        enabled &= Supported;
        _layoutTimer.Change(enabled ? 0 : Timeout.Infinite, 1000);
        Post(() =>
        {
            _enabled = enabled;
            _side = side;
            Refresh();
        });
    }

    public void Update(SensorSnapshot s)
    {
        var rows = BuildRows(s);
        Post(() =>
        {
            _rows = rows;
            Refresh();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _layoutTimer.Dispose();
        Post(() =>
        {
            _panel.DestroyHandle();
            _menu.Dispose();
            _style?.Dispose();
            Forms.Application.ExitThread();
        });
        _disposed = true;
        _thread.Join(1000);
    }

    void Post(Action a)
    {
        if (_disposed)
            return;
        try { _invoker.BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    // ───────────────────────────── 数据 ─────────────────────────────

    /// <summary>三块的内容，每块最多两行；顺序即从左到右的摆放顺序。</summary>
    static List<BarRow>[] BuildRows(SensorSnapshot s)
    {
        List<BarRow> cpu = [], gpu = [], net = [];
        AddRow(cpu, "CPU", Percent(SensorPicks.CpuLoad(s)), Temp(SensorPicks.CpuTemp(s)));
        AddRow(cpu, "内存", Percent(SensorPicks.MemLoad(s)));
        AddRow(gpu, "GPU", Percent(SensorPicks.GpuLoad(s)), Temp(SensorPicks.GpuTemp(s)));
        AddRow(gpu, "显存", Percent(SensorPicks.GpuMemLoad(s)));
        if (SensorPicks.NetRates(s) is (float up, float down))
        {
            AddRow(net, "↑", LabelTone.Up, Rate(up));
            AddRow(net, "↓", LabelTone.Down, Rate(down));
        }
        return [cpu, gpu, net];
    }

    static BarCell? Percent(SensorReading? r) =>
        r?.Value is float v ? new BarCell($"{v:0}", "%", "100", "%", v >= 90 ? 2 : v >= 75 ? 1 : 0, Math.Clamp(v / 100, 0, 1)) : null;

    static BarCell? Temp(SensorReading? r) =>
        r?.Value is float v ? new BarCell($"{v:0}", "°C", "100", "°C", v >= 85 ? 2 : v >= 70 ? 1 : 0) : null;

    /// <summary>“3.1 KB/s” 拆成数字和单位两部分。</summary>
    static BarCell Rate(float v)
    {
        var s = SensorFormat.Rate(v);
        int i = s.LastIndexOf(' ');
        return new BarCell(s[..i], s[(i + 1)..], "999.9", "MB/s");
    }

    static void AddRow(List<BarRow> rows, string label, params BarCell?[] cells) => AddRow(rows, label, LabelTone.Normal, cells);

    static void AddRow(List<BarRow> rows, string label, LabelTone tone, params BarCell?[] cells)
    {
        var present = cells.Where(c => c.HasValue).Select(c => c!.Value).ToArray();
        if (present.Length > 0)
            rows.Add(new BarRow(label, present, tone));
    }

    // ───────────────────────────── 布局（计时器线程） ─────────────────────────────

    /// <summary>
    /// 任务栏的几何信息，坐标都是屏幕物理像素。
    /// LeftStart..LeftEnd 是居中图标组左边的空白，RightStart..RightEnd 是图标组右边到托盘之间的空白。
    /// </summary>
    sealed record Layout(IntPtr Taskbar, int BarLeft, int BarTop, int BarHeight,
        int LeftStart, int LeftEnd, int RightStart, int RightEnd, int Dpi, bool Light);

    void PollLayout()
    {
        if (Interlocked.Exchange(ref _layoutBusy, 1) == 1)
            return;
        try
        {
            var layout = QueryLayout();
            Post(() =>
            {
                if (layout == _layout)
                    return;
                _layout = layout;
                Refresh();
            });
        }
        catch { }
        finally
        {
            _layoutBusy = 0;
        }
    }

    Layout? QueryLayout()
    {
        IntPtr bar = Native.FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || !Native.GetWindowRect(bar, out var rb))
            return null;
        IntPtr tray = Native.FindWindowEx(bar, 0, "TrayNotifyWnd", null);
        int rightEnd = tray != 0 && Native.GetWindowRect(tray, out var rt) ? rt.Left : rb.Right;
        int leftStart = rb.Left, leftEnd, rightStart;

        var buttons = FrameButtons(bar);
        int startIndex = buttons?.FindIndex(b => b.Id == "StartButton") ?? -1;
        if (buttons != null && startIndex >= 0)
        {
            // 从开始按钮往右，首尾相接的按钮都算图标组；隔开一段的（比如 Copilot）当作右侧空白的边界
            var start = buttons[startIndex].Rect;
            double right = start.Right, tolerance = (rb.Bottom - rb.Top) / 4.0;
            foreach (var (_, r) in buttons.Where(b => b.Rect.Left >= start.Left))
            {
                if (r.Left > right + tolerance)
                {
                    rightEnd = Math.Min(rightEnd, (int)r.Left);
                    break;
                }
                right = Math.Max(right, r.Right);
            }
            // 开始按钮左边的按钮（小组件）挡住左侧空白的起点
            foreach (var (_, r) in buttons.Where(b => b.Rect.Right <= start.Left + 1))
                leftStart = Math.Max(leftStart, (int)r.Right);
            leftEnd = (int)start.Left;
            rightStart = (int)Math.Ceiling(right);
        }
        else
        {
            // UI 自动化不可用：用开始按钮的窗口位置估算，图标组在任务栏上居中，左右对称
            IntPtr startWnd = Native.FindWindowEx(bar, 0, "Start", null);
            if (startWnd == 0 || !Native.GetWindowRect(startWnd, out var rs))
                return null;
            leftEnd = rs.Left;
            rightStart = rb.Left + rb.Right - rs.Left;
        }

        return new Layout(bar, rb.Left, rb.Top, rb.Bottom - rb.Top, leftStart, leftEnd, rightStart, rightEnd,
            (int)Native.GetDpiForWindow(bar), SystemUsesLightTheme());
    }

    /// <summary>任务栏图标区（TaskbarFrame）里每个按钮的位置，按从左到右排序。</summary>
    List<(string Id, System.Windows.Rect Rect)>? FrameButtons(IntPtr bar)
    {
        try
        {
            if (_frame == null || _frameOwner != bar)
            {
                _frameOwner = bar;
                _frame = AutomationElement.FromHandle(bar).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskbarFrame"));
                if (_frame == null)
                    return null;
            }
            var request = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            using (request.Activate())
            {
                return _frame.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
                    .Select(e => (e.Cached.AutomationId, e.Cached.BoundingRectangle))
                    .Where(b => !b.BoundingRectangle.IsEmpty && b.BoundingRectangle.Width > 0)
                    .OrderBy(b => b.BoundingRectangle.Left)
                    .ToList();
            }
        }
        catch
        {
            _frame = null; // explorer 重启等情况下缓存的元素失效，下次重新找
            return null;
        }
    }

    /// <summary>任务栏跟随的是“Windows 模式”（不是应用模式）的深浅色。</summary>
    static bool SystemUsesLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    // ───────────────────────────── 摆放与绘制（窗口线程） ─────────────────────────────

    void Refresh()
    {
        var l = _layout;
        if (!_enabled || l == null || _rows.All(r => r.Count == 0))
        {
            _panel.Hide();
            return;
        }

        if (_style == null || _style.Dpi != l.Dpi)
        {
            _style?.Dispose();
            _style = new BarStyle(l.Dpi);
        }
        var st = _style;
        int margin = st.Scale(8);
        var blocks = _rows.Where(r => r.Count > 0).ToList();

        // 选了左侧但连第一块都放不下（任务栏靠左对齐时左边没有空白），改放右侧
        bool left = _side == TaskbarSide.Left && st.Measure([blocks[0]]) + 2 * margin <= l.LeftEnd - l.LeftStart;
        int start = left ? l.LeftStart : l.RightStart, end = left ? l.LeftEnd : l.RightEnd;
        while (blocks.Count > 0 && st.Measure(blocks) + 2 * margin > end - start)
            blocks.RemoveAt(blocks.Count - 1);
        if (blocks.Count == 0)
        {
            _panel.Hide();
            return;
        }

        // 所有块画在同一个子窗口里（悬停、点击都是一个整体）；左侧靠左，右侧贴着托盘
        int width = st.Measure(blocks);
        _panel.Show(l, st, blocks, left ? start + margin : end - margin - width, width);
    }

    void OnClick() => _show();

    void OnMenu()
    {
        _moveItem.Text = _side == TaskbarSide.Left ? "移到右侧" : "移到左侧";
        Native.SetForegroundWindow(_invoker.Handle); // 否则点别处时菜单不会自动关闭
        _menu.Show(Forms.Cursor.Position);
    }

    /// <summary>按 DPI 缓存的字体和尺寸，负责测量和绘制内容。</summary>
    sealed class BarStyle : IDisposable
    {
        // 标签用雅黑（中文）；数字用 Segoe UI Semibold（等宽数字，粗一点更醒目）；单位小一号、淡一些
        readonly Font _label, _value, _unit;
        readonly StringFormat _format;
        readonly Graphics _measure;
        readonly Bitmap _measureBmp = new(1, 1);
        readonly Dictionary<(Font, string), float> _widths = [];
        readonly float _ascent, _rowHeight;

        public BarStyle(int dpi)
        {
            Dpi = dpi;
            _label = new Font("Microsoft YaHei UI", 12f * dpi / 96, FontStyle.Regular, GraphicsUnit.Pixel);
            _value = new Font("Segoe UI Semibold", 13f * dpi / 96, FontStyle.Regular, GraphicsUnit.Pixel);
            _unit = new Font("Segoe UI", 11f * dpi / 96, FontStyle.Regular, GraphicsUnit.Pixel);
            _format = (StringFormat)StringFormat.GenericTypographic.Clone();
            _format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            _measure = Graphics.FromImage(_measureBmp);
            _measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // 不同字体按基线对齐：行高 = 最大上部 + 最大下部
            Font[] fonts = [_label, _value, _unit];
            _ascent = fonts.Max(Ascent);
            _rowHeight = _ascent + fonts.Max(Descent);
        }

        public int Dpi { get; }

        public int Scale(double v) => (int)Math.Round(v * Dpi / 96);

        static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);
        static float Descent(Font f) => f.Size * f.FontFamily.GetCellDescent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

        /// <summary>两端留白；标签到第一列、列与列之间的间距；块与块之间的距离（中间画分隔线）。</summary>
        int Pad => Scale(6);
        int LabelGap => Scale(8);
        int ColumnGap => Scale(10);
        int BlockGap => Scale(22);
        /// <summary>占用率小条的尺寸和它到数字的距离；数字到单位的距离。</summary>
        int MeterWidth => Scale(26);
        int MeterHeight => Math.Max(3, Scale(4));
        int MeterGap => Scale(6);
        int UnitGap => Scale(1.5);

        float Width(Font font, string s)
        {
            if (s.Length == 0)
                return 0;
            if (!_widths.TryGetValue((font, s), out var w))
                _widths[(font, s)] = w = _measure.MeasureString(s, font, int.MaxValue, _format).Width;
            return w;
        }

        float LabelWidth(List<BarRow> rows) => rows.Max(r => Width(_label, r.Label));

        /// <summary>一列由三段组成：小条（这一列有的话）、右对齐的数字、单位。</summary>
        readonly record struct Column(float Meter, float Number, float Unit)
        {
            public float Width => Meter + Number + Unit;
        }

        /// <summary>各数值列的宽度：同一列取所有行里最宽的那格，上下两行对齐。</summary>
        Column[] Columns(List<BarRow> rows)
        {
            var cols = new Column[rows.Max(r => r.Cells.Length)];
            foreach (var row in rows)
                for (int i = 0; i < row.Cells.Length; i++)
                {
                    var c = row.Cells[i];
                    cols[i] = new Column(
                        Math.Max(cols[i].Meter, c.Fill.HasValue ? MeterWidth + MeterGap : 0),
                        Math.Max(cols[i].Number, Math.Max(Width(_value, c.Template), Width(_value, c.Value))),
                        Math.Max(cols[i].Unit, UnitGap + Math.Max(Width(_unit, c.UnitTemplate), Width(_unit, c.Unit))));
                }
            return cols;
        }

        float BlockWidth(List<BarRow> rows)
        {
            var cols = Columns(rows);
            return LabelWidth(rows) + LabelGap + cols.Sum(c => c.Width) + ColumnGap * (cols.Length - 1);
        }

        /// <summary>几块并排的总宽度（含两端留白）。</summary>
        public int Measure(List<List<BarRow>> blocks) =>
            (int)Math.Ceiling(Pad * 2 + blocks.Sum(BlockWidth) + BlockGap * (blocks.Count - 1));

        public void Draw(Graphics g, List<List<BarRow>> blocks, int width, int height, bool light, bool hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // 平时用几乎透明的底色，整块都能接收鼠标；悬停时像任务栏按钮一样淡淡高亮
            using (var bg = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                g.FillRectangle(bg, 0, 0, width, height);
            if (hover)
            {
                using var hl = new SolidBrush(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
                using var path = RoundedRect(new RectangleF(Scale(2), Scale(4), width - Scale(4), height - Scale(8)), Scale(5));
                g.FillPath(hl, path);
            }

            Color Ink(int alpha) => light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255);
            using var textBrush = new SolidBrush(Ink(light ? 235 : 245));
            using var labelBrush = new SolidBrush(Ink(light ? 160 : 175));
            using var unitBrush = new SolidBrush(Ink(light ? 135 : 150));
            using var trackBrush = new SolidBrush(Ink(light ? 38 : 48));
            using var lineBrush = new SolidBrush(Ink(light ? 45 : 55));
            using var accentBrush = new SolidBrush(light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255));
            using var upBrush = new SolidBrush(light ? Color.FromArgb(16, 124, 16) : Color.FromArgb(108, 214, 120));
            using var warmBrush = new SolidBrush(light ? Color.FromArgb(200, 110, 0) : Color.FromArgb(255, 185, 70));
            using var hotBrush = new SolidBrush(light ? Color.FromArgb(205, 30, 30) : Color.FromArgb(255, 100, 100));

            float left = Pad;
            for (int b = 0; b < blocks.Count; b++)
            {
                if (b > 0)
                {
                    // 块与块之间一条细竖线
                    float lineX = (float)Math.Round(left - BlockGap / 2f);
                    g.FillRectangle(lineBrush, lineX, height * 0.28f, Math.Max(1, Scale(1)), height * 0.44f);
                }
                DrawBlock(blocks[b], left);
                left += BlockWidth(blocks[b]) + BlockGap;
            }

            void DrawBlock(List<BarRow> rows, float x0)
            {
                float top = (height - _rowHeight * rows.Count) / 2;
                float labelW = LabelWidth(rows);
                var cols = Columns(rows);
                foreach (var row in rows)
                {
                    float baseline = top + _ascent;
                    var tone = row.Tone == LabelTone.Up ? upBrush : row.Tone == LabelTone.Down ? accentBrush : labelBrush;
                    Text(row.Label, _label, tone, x0, baseline);
                    float x = x0 + labelW + LabelGap;
                    for (int i = 0; i < row.Cells.Length; i++)
                    {
                        var c = row.Cells[i];
                        var level = c.Level == 2 ? hotBrush : c.Level == 1 ? warmBrush : null;
                        if (c.Fill is float fill)
                        {
                            // 小条竖直方向对准数字的中部
                            float h = MeterHeight, y = (float)Math.Round(baseline - _value.Size * 0.36f - h / 2);
                            var track = new RectangleF(x, y, MeterWidth, h);
                            using (var p = RoundedRect(track, h / 2))
                                g.FillPath(trackBrush, p);
                            if (fill > 0)
                                using (var p = RoundedRect(track with { Width = Math.Max(h, MeterWidth * fill) }, h / 2))
                                    g.FillPath(level ?? accentBrush, p);
                        }
                        x += cols[i].Meter;
                        // 数字在列内右对齐，单位紧跟其后
                        Text(c.Value, _value, level ?? textBrush, x + cols[i].Number - Width(_value, c.Value), baseline);
                        x += cols[i].Number;
                        Text(c.Unit, _unit, unitBrush, x + UnitGap, baseline);
                        x += cols[i].Unit + ColumnGap;
                    }
                    top += _rowHeight;
                }
            }

            void Text(string s, Font font, Brush brush, float x, float baseline) =>
                g.DrawString(s, font, brush, x, baseline - Ascent(font), _format);
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            _label.Dispose();
            _value.Dispose();
            _unit.Dispose();
            _format.Dispose();
            _measure.Dispose();
            _measureBmp.Dispose();
        }
    }

    /// <summary>任务栏里显示数据的子窗口（分层窗口，逐像素透明）。</summary>
    sealed class PanelWindow(TaskbarPanel owner) : Forms.NativeWindow
    {
        IntPtr _parent;
        bool _hover;
        Layout? _layout;
        BarStyle? _style;
        List<List<BarRow>> _blocks = [];
        int _width;

        public void Show(Layout l, BarStyle style, List<List<BarRow>> blocks, int screenX, int width)
        {
            // 任务栏重建（explorer 重启）后旧的子窗口会随之销毁，这里重新建
            if (Handle == 0 || _parent != l.Taskbar || !Native.IsWindow(Handle))
            {
                if (Handle != 0)
                    DestroyHandle();
                _parent = l.Taskbar;
                try
                {
                    CreateHandle(new Forms.CreateParams
                    {
                        Caption = "SysMonitor",
                        Parent = l.Taskbar,
                        Style = Native.WS_CHILD | Native.WS_CLIPSIBLINGS,
                        // NOPARENTNOTIFY：不然按下左键时任务栏收到通知会抢走鼠标，松开的消息到不了这里
                        ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_NOPARENTNOTIFY,
                    });
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return;
                }
            }

            _layout = l;
            _style = style;
            _blocks = blocks;
            _width = width;
            var p = new Native.POINT { X = screenX, Y = l.BarTop };
            Native.ScreenToClient(l.Taskbar, ref p);
            Native.SetWindowPos(Handle, Native.HWND_TOP, p.X, p.Y, width, l.BarHeight, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Render();
        }

        public void Hide()
        {
            if (Handle != 0)
                Native.ShowWindow(Handle, 0);
            _hover = false;
        }

        void Render()
        {
            if (Handle == 0 || _layout == null || _style == null)
                return;
            int w = _width, h = _layout.BarHeight;
            var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(0);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr dib = Native.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, 0, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            try
            {
                using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
                using (var g = Graphics.FromImage(bmp))
                    _style.Draw(g, _blocks, w, h, _layout.Light, _hover);

                var size = new Native.SIZE { Cx = w, Cy = h };
                var src = new Native.POINT();
                var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
                Native.UpdateLayeredWindow(Handle, screen, 0, ref size, mem, ref src, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteObject(dib);
                Native.DeleteDC(mem);
                Native.ReleaseDC(0, screen);
            }
        }

        protected override void WndProc(ref Forms.Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = Native.MA_NOACTIVATE;
                    return;
                case Native.WM_MOUSEMOVE:
                    if (!_hover)
                    {
                        _hover = true;
                        var t = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(), dwFlags = 2, hwndTrack = Handle }; // TME_LEAVE
                        Native.TrackMouseEvent(ref t);
                        Render();
                    }
                    break;
                case Native.WM_MOUSELEAVE:
                    _hover = false;
                    Render();
                    break;
                case Native.WM_LBUTTONDOWN:
                    Native.SetCapture(Handle); // 按下后捕获鼠标，保证松开的消息回到这里
                    return;
                case Native.WM_LBUTTONUP:
                {
                    Native.ReleaseCapture();
                    // 按下后拖到外面再松开不算点击
                    int cx = (short)m.LParam, cy = (short)(m.LParam >> 16);
                    if (Native.GetClientRect(Handle, out var rc) && cx >= 0 && cy >= 0 && cx < rc.Right && cy < rc.Bottom)
                        owner.OnClick();
                    return;
                }
                case Native.WM_RBUTTONUP:
                    owner.OnMenu();
                    return;
            }
            base.WndProc(ref m);
        }
    }

    static class Native
    {
        public const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_NOPARENTNOTIFY = 0x4;
        public const int WM_MOUSEACTIVATE = 0x21, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3;
        public const int MA_NOACTIVATE = 3;
        public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOP = 0;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Cx, Cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE size, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }
}
