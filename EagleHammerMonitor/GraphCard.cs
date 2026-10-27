using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace EagleHammerMonitor;

/// <summary>
/// 曲线卡片。直接在 OnRender 里画（一个元素一次绘制），
/// 而不是用 Polyline/Line/TextBlock 等大量元素拼，开销小得多。
/// </summary>
public sealed class GraphCard : FrameworkElement
{
    public static bool Dark { get; set; } = true;

    static readonly Color[] DarkPalette = [C(0x4CC2FF), C(0xFF8C42), C(0x6EE07A), C(0xE06ED8), C(0xF2D04B), C(0xFF5F5F)];
    static readonly Color[] LightPalette = [C(0x0067C0), C(0xD9640A), C(0x2E9E44), C(0xB4329E), C(0xB98900), C(0xD13438)];
    static readonly Typeface Face = new("Microsoft YaHei UI");
    static readonly Typeface FaceBold = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(GraphModel), typeof(GraphCard),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnModelChanged));

    public GraphModel? Model
    {
        get => (GraphModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (GraphCard)d;
        if (e.OldValue is GraphModel old)
            old.Changed -= card.InvalidateVisual;
        if (e.NewValue is GraphModel m)
            m.Changed += card.InvalidateVisual;
    }

    static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    static SolidColorBrush B(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var m = Model;
        double w = ActualWidth, h = ActualHeight;
        if (m == null || w < 50 || h < 50)
            return;

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var line = (Dark ? DarkPalette : LightPalette)[m.ColorIndex % DarkPalette.Length];
        var card = B(Dark ? C(0x1E1E25) : C(0xFFFFFF));
        var text = B(Dark ? C(0xE6E6EA) : C(0x1F1F1F));
        var dim = B(Dark ? C(0x8C8C98) : C(0x707078));
        var gridPen = new Pen(B(Dark ? Colors.White : Colors.Black, 0.07), 1);
        var borderPen = new Pen(B(Dark ? Colors.White : Colors.Black, 0.13), 1);
        var linePen = new Pen(B(line), 1.8) { LineJoin = PenLineJoin.Round };
        var fill = B(line, Dark ? 0.18 : 0.12);

        FormattedText T(string s, double size, Brush brush, bool bold = false) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, dip);

        dc.DrawRoundedRectangle(card, borderPen, new Rect(0.5, 0.5, w - 1, h - 1), 6, 6);
        const double pad = 12;

        // 当前值（右上角大号）
        var values = new List<float>(m.Count);
        for (int i = 0; i < m.Count; i++)
            if (m.At(i) is float v && float.IsFinite(v))
                values.Add(v);
        var current = T(SensorFormat.Format(m.Type, m.Count > 0 ? m.At(m.Count - 1) : null), 22, B(line), true);
        dc.DrawText(current, new Point(w - pad - current.Width, pad - 4));

        var title = T(m.Title, 13, text, true);
        title.MaxTextWidth = Math.Max(10, w - pad * 3 - current.Width);
        title.MaxLineCount = 1;
        title.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(title, new Point(pad, pad));

        double y0 = pad + title.Height + 2;
        if (values.Count > 0)
        {
            var stats = T($"最小 {SensorFormat.Format(m.Type, values.Min())}    平均 {SensorFormat.Format(m.Type, values.Average())}    最大 {SensorFormat.Format(m.Type, values.Max())}", 12, dim);
            dc.DrawText(stats, new Point(pad, y0));
        }

        // 绘图区
        const double axisW = 70;
        var plot = new Rect(new Point(pad, y0 + 26), new Point(w - pad - axisW, h - pad));
        if (plot.Width < 20 || plot.Height < 20)
            return;

        var (lo, hi) = Range(m.Type, values);
        for (int g = 0; g <= 4; g++)
        {
            double y = plot.Top + plot.Height * g / 4;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = T(SensorFormat.Format(m.Type, (float)(hi - (hi - lo) * g / 4)), 11, dim);
            dc.DrawText(label, new Point(plot.Right + 6, y - label.Height / 2));
        }
        for (int k = 30; k < GraphModel.Capacity; k += 30)
        {
            double x = plot.Right - plot.Width * k / (GraphModel.Capacity - 1);
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        dc.DrawRectangle(null, borderPen, plot);

        // 折线 + 填充（无数据处断开）
        double step = plot.Width / (GraphModel.Capacity - 1);
        var seg = new List<Point>();
        dc.PushClip(new RectangleGeometry(plot));

        void Flush()
        {
            if (seg.Count >= 2)
            {
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(seg[0].X, plot.Bottom), true, true);
                    ctx.PolyLineTo(seg, false, false);
                    ctx.LineTo(new Point(seg[^1].X, plot.Bottom), false, false);
                }
                area.Freeze();
                dc.DrawGeometry(fill, null, area);

                var path = new StreamGeometry();
                using (var ctx = path.Open())
                {
                    ctx.BeginFigure(seg[0], false, false);
                    ctx.PolyLineTo(seg.Skip(1).ToList(), true, true);
                }
                path.Freeze();
                dc.DrawGeometry(null, linePen, path);
            }
            seg.Clear();
        }

        for (int i = 0; i < m.Count; i++)
        {
            if (m.At(i) is not float v || !float.IsFinite(v))
            {
                Flush();
                continue;
            }
            double x = plot.Right - (m.Count - 1 - i) * step;
            double y = plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
            seg.Add(new Point(x, y));
        }
        Flush();
        dc.Pop();
    }

    static (double Lo, double Hi) Range(SensorType type, List<float> values)
    {
        if (SensorFormat.IsPercent(type))
            return (0, Math.Max(100, values.Count > 0 ? values.Max() : 0));
        if (values.Count == 0)
            return (0, 1);
        double min = values.Min(), max = values.Max();
        double margin = Math.Max((max - min) * 0.15, Math.Max(Math.Abs(max) * 0.05, 0.5));
        double lo = min - margin, hi = max + margin;
        if (min >= 0 && lo < 0)
            lo = 0;
        return (lo, hi);
    }
}
