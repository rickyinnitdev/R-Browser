using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace R1.UI;

internal static class Theme
{
    public static readonly Color Bg0 = Color.FromArgb(9, 10, 13);
    public static readonly Color Bg1 = Color.FromArgb(15, 16, 20);
    public static readonly Color Bg2 = Color.FromArgb(23, 24, 29);
    public static readonly Color Stroke = Color.FromArgb(44, 46, 53);
    public static readonly Color StrokeSoft = Color.FromArgb(28, 32, 42);
    public static readonly Color Text = Color.FromArgb(233, 238, 248);
    public static readonly Color Muted = Color.FromArgb(139, 149, 168);
    public static readonly Color Dim = Color.FromArgb(96, 105, 122);
    public static readonly Color Accent = Color.FromArgb(170, 185, 210);
    public static readonly Color AccentSoft = Color.FromArgb(48, 64, 112);
    public static readonly Color Aqua = Color.FromArgb(155, 207, 192);
    public static readonly Color Danger = Color.FromArgb(255, 107, 125);
    public static readonly Color Hover = Color.FromArgb(28, 33, 43);

    private static string _uiFamily = "Segoe UI";
    private static string _displayFamily = "Segoe UI";

    static Theme()
    {
        using var fonts = new InstalledFontCollection();
        var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains("Segoe UI Variable Text")) _uiFamily = "Segoe UI Variable Text";
        if (names.Contains("Segoe UI Variable Display")) _displayFamily = "Segoe UI Variable Display";
        else _displayFamily = _uiFamily;
    }

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new(_uiFamily, size, style, GraphicsUnit.Point);
    public static Font Display(float size, FontStyle style = FontStyle.Regular) => new(_displayFamily, size, style, GraphicsUnit.Point);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        using var p = Round(r, radius);
        using var b = new SolidBrush(color);
        g.FillPath(b, p);
    }

    public static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
    {
        using var p = Round(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, p);
    }

    public static void Glow(Graphics g, RectangleF r, float radius, Color color)
    {
        // soft radial-ish bloom made of concentric alpha rings
        for (int i = 6; i >= 1; i--)
        {
            float grow = i * 3.5f;
            var rr = RectangleF.Inflate(r, grow, grow);
            int a = Math.Max(2, 16 / i);
            using var p = Round(rr, radius + grow);
            using var pen = new Pen(Color.FromArgb(a, color), 1.2f);
            g.DrawPath(pen, p);
        }
    }

    public static void Bloom(Graphics g, RectangleF bounds, Color center)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = center,
            SurroundColors = new[] { Color.FromArgb(0, center) },
            FocusScales = new PointF(0.45f, 0.45f)
        };
        g.FillPath(brush, path);
    }

    public static Color Mix(Color a, Color b, float t)    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static string Ellipsize(Graphics g, string text, Font font, float width)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (g.MeasureString(text, font).Width <= width) return text;
        const string dots = "\u2026";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (g.MeasureString(text[..mid] + dots, font).Width <= width) lo = mid;
            else hi = mid - 1;
        }
        return text[..Math.Max(0, lo)] + dots;
    }
}

internal enum IconKind
{
    Back, Forward, Reload, Stop, Plus, Close, Minimize, Maximize, Restore, Star, StarFilled,
    Shield, ShieldFilled, Menu, Search, Lock, Globe, Download, Bookmark, History, Trash,
    Home, ChevronDown, External, Info, Spark, Cookie, Eye, Sliders, Volume, VolumeMute,
    LayoutVertical, LayoutHorizontal, CollapseLeft, ExpandRight
}

internal static class Icons
{
    public static void Draw(Graphics g, IconKind kind, RectangleF box, Color color, float weight = 1.7f)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = box.Width / 24f;
        var state = g.Save();
        g.TranslateTransform(box.X, box.Y);
        g.ScaleTransform(s, s);

        using var pen = new Pen(color, weight)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(color);

        switch (kind)
        {
            case IconKind.Back:
                g.DrawLines(pen, new[] { P(15, 5), P(8, 12), P(15, 19) });
                break;
            case IconKind.Forward:
                g.DrawLines(pen, new[] { P(9, 5), P(16, 12), P(9, 19) });
                break;
            case IconKind.Reload:
                g.DrawArc(pen, 5, 5, 14, 14, 40, 280);
                using (var arr = new GraphicsPath())
                {
                    arr.AddPolygon(new[] { P(16.2f, 2.4f), P(20.6f, 6.0f), P(15.4f, 7.6f) });
                    g.FillPath(fill, arr);
                }
                break;
            case IconKind.Stop:
                g.DrawLine(pen, 7, 7, 17, 17);
                g.DrawLine(pen, 17, 7, 7, 17);
                break;
            case IconKind.Plus:
                g.DrawLine(pen, 12, 6, 12, 18);
                g.DrawLine(pen, 6, 12, 18, 12);
                break;
            case IconKind.Close:
                g.DrawLine(pen, 7.5f, 7.5f, 16.5f, 16.5f);
                g.DrawLine(pen, 16.5f, 7.5f, 7.5f, 16.5f);
                break;
            case IconKind.Minimize:
                g.DrawLine(pen, 6, 12.5f, 18, 12.5f);
                break;
            case IconKind.Maximize:
                using (var p = Theme.Round(new RectangleF(6.5f, 6.5f, 11, 11), 2f))
                    g.DrawPath(pen, p);
                break;
            case IconKind.Restore:
                using (var p = Theme.Round(new RectangleF(8.5f, 5.5f, 10, 10), 2f))
                    g.DrawPath(pen, p);
                using (var p = Theme.Round(new RectangleF(5.5f, 8.5f, 10, 10), 2f))
                {
                    using var bg = new SolidBrush(Color.FromArgb(255, 20, 24, 31));
                    g.FillPath(bg, p);
                    g.DrawPath(pen, p);
                }
                break;
            case IconKind.Star:
            case IconKind.StarFilled:
                var star = StarPoints(12, 12, 9.2f, 4.1f);
                if (kind == IconKind.StarFilled) g.FillPolygon(fill, star);
                else g.DrawPolygon(pen, star);
                break;
            case IconKind.Shield:
            case IconKind.ShieldFilled:
            {
                using var shield = ShieldPath();
                if (kind == IconKind.ShieldFilled)
                {
                    g.FillPath(fill, shield);
                }
                else g.DrawPath(pen, shield);
                break;
            }
            case IconKind.Menu:
                g.DrawLine(pen, 5, 7, 19, 7);
                g.DrawLine(pen, 5, 12, 19, 12);
                g.DrawLine(pen, 5, 17, 19, 17);
                break;
            case IconKind.Search:
                g.DrawEllipse(pen, 5, 5, 11.5f, 11.5f);
                g.DrawLine(pen, 15.4f, 15.4f, 19.5f, 19.5f);
                break;
            case IconKind.Lock:
                using (var p = Theme.Round(new RectangleF(6, 11, 12, 9), 2.2f))
                    g.DrawPath(pen, p);
                g.DrawArc(pen, 8.2f, 4.6f, 7.6f, 9.4f, 180, 180);
                break;
            case IconKind.Globe:
                g.DrawEllipse(pen, 4, 4, 16, 16);
                g.DrawEllipse(pen, 9, 4, 6, 16);
                g.DrawLine(pen, 4.4f, 12, 19.6f, 12);
                break;
            case IconKind.Download:
                g.DrawLine(pen, 12, 4, 12, 15);
                g.DrawLines(pen, new[] { P(7.6f, 11), P(12, 15.4f), P(16.4f, 11) });
                g.DrawLines(pen, new[] { P(5, 17), P(5, 19.6f), P(19, 19.6f), P(19, 17) });
                break;
            case IconKind.Bookmark:
                g.DrawLines(pen, new[] { P(7, 4), P(17, 4), P(17, 20), P(12, 15.6f), P(7, 20), P(7, 4) });
                break;
            case IconKind.History:
                g.DrawEllipse(pen, 4.5f, 4.5f, 15, 15);
                g.DrawLines(pen, new[] { P(12, 8), P(12, 12.4f), P(15.4f, 14.4f) });
                break;
            case IconKind.Trash:
                g.DrawLine(pen, 5, 7, 19, 7);
                g.DrawLines(pen, new[] { P(9, 7), P(9, 4.6f), P(15, 4.6f), P(15, 7) });
                using (var p = Theme.Round(new RectangleF(6.6f, 7, 10.8f, 13), 2f))
                    g.DrawPath(pen, p);
                g.DrawLine(pen, 10.4f, 10.6f, 10.4f, 16.4f);
                g.DrawLine(pen, 13.6f, 10.6f, 13.6f, 16.4f);
                break;
            case IconKind.Home:
                g.DrawLines(pen, new[] { P(4.5f, 11), P(12, 4.5f), P(19.5f, 11) });
                g.DrawLines(pen, new[] { P(6.5f, 10), P(6.5f, 19.5f), P(17.5f, 19.5f), P(17.5f, 10) });
                break;
            case IconKind.ChevronDown:
                g.DrawLines(pen, new[] { P(7, 10), P(12, 15), P(17, 10) });
                break;
            case IconKind.External:
                g.DrawLines(pen, new[] { P(14, 5), P(19.5f, 5), P(19.5f, 10.5f) });
                g.DrawLine(pen, 19.5f, 5, 11.5f, 13);
                g.DrawLines(pen, new[] { P(18, 14), P(18, 19), P(5, 19), P(5, 6), P(10, 6) });
                break;
            case IconKind.Info:
                g.DrawEllipse(pen, 4.5f, 4.5f, 15, 15);
                g.DrawLine(pen, 12, 11, 12, 16);
                g.FillEllipse(fill, 11.1f, 7.2f, 1.8f, 1.8f);
                break;
            case IconKind.Spark:
                g.DrawLines(pen, new[] { P(12, 3.5f), P(13.8f, 9.4f), P(19.5f, 12), P(13.8f, 14.6f), P(12, 20.5f), P(10.2f, 14.6f), P(4.5f, 12), P(10.2f, 9.4f), P(12, 3.5f) });
                break;
            case IconKind.Cookie:
                g.DrawEllipse(pen, 4.5f, 4.5f, 15, 15);
                g.FillEllipse(fill, 9, 9, 1.7f, 1.7f);
                g.FillEllipse(fill, 14, 11, 1.7f, 1.7f);
                g.FillEllipse(fill, 10.4f, 14.4f, 1.7f, 1.7f);
                break;
            case IconKind.Eye:
                g.DrawBezier(pen, 3.5f, 12, 8f, 5.8f, 16f, 5.8f, 20.5f, 12);
                g.DrawBezier(pen, 20.5f, 12, 16f, 18.2f, 8f, 18.2f, 3.5f, 12);
                g.DrawEllipse(pen, 9.6f, 9.6f, 4.8f, 4.8f);
                break;
            case IconKind.Sliders:
                g.DrawLine(pen, 4, 8, 20, 8);
                g.DrawLine(pen, 4, 16, 20, 16);
                g.FillEllipse(fill, 8.4f, 5.6f, 3.2f, 3.2f);
                g.FillEllipse(fill, 13.4f, 13.6f, 3.2f, 3.2f);
                break;
            case IconKind.Volume:
                g.DrawLines(pen, new[] { P(4.5f, 9.5f), P(8.5f, 9.5f), P(13.5f, 5.5f), P(13.5f, 18.5f), P(8.5f, 14.5f), P(4.5f, 14.5f), P(4.5f, 9.5f) });
                g.DrawArc(pen, 14f, 8.5f, 6f, 7f, -45, 90);
                break;
            case IconKind.VolumeMute:
                g.DrawLines(pen, new[] { P(4.5f, 9.5f), P(8.5f, 9.5f), P(13.5f, 5.5f), P(13.5f, 18.5f), P(8.5f, 14.5f), P(4.5f, 14.5f), P(4.5f, 9.5f) });
                g.DrawLine(pen, 16f, 9.5f, 20.5f, 14.5f);
                g.DrawLine(pen, 20.5f, 9.5f, 16f, 14.5f);
                break;
            case IconKind.LayoutVertical:
                using (var p = Theme.Round(new RectangleF(4.5f, 4.5f, 15, 15), 2.2f))
                    g.DrawPath(pen, p);
                g.DrawLine(pen, 9.5f, 4.5f, 9.5f, 19.5f);
                break;
            case IconKind.LayoutHorizontal:
                using (var p = Theme.Round(new RectangleF(4.5f, 4.5f, 15, 15), 2.2f))
                    g.DrawPath(pen, p);
                g.DrawLine(pen, 4.5f, 9.5f, 19.5f, 9.5f);
                break;
            case IconKind.CollapseLeft:
                g.DrawLines(pen, new[] { P(13.5f, 7), P(8.5f, 12), P(13.5f, 17) });
                g.DrawLine(pen, 6.5f, 6.5f, 6.5f, 17.5f);
                break;
            case IconKind.ExpandRight:
                g.DrawLines(pen, new[] { P(10.5f, 7), P(15.5f, 12), P(10.5f, 17) });
                g.DrawLine(pen, 17.5f, 6.5f, 17.5f, 17.5f);
                break;
        }
        g.Restore(state);
        g.SmoothingMode = old;
    }

    private static PointF P(float x, float y) => new(x, y);

    private static PointF[] StarPoints(float cx, float cy, float outer, float inner)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2 + i * Math.PI / 5;
            float r = (i % 2 == 0) ? outer : inner;
            pts[i] = new PointF(cx + (float)Math.Cos(angle) * r, cy + (float)Math.Sin(angle) * r);
        }
        return pts;
    }

    private static GraphicsPath ShieldPath()
    {
        var p = new GraphicsPath();
        p.AddLines(new[]
        {
            new PointF(12, 3.2f), new PointF(19.2f, 6.2f), new PointF(19.2f, 11.4f)
        });
        p.AddBezier(19.2f, 11.4f, 19.2f, 16.4f, 15.8f, 19.6f, 12, 21f);
        p.AddBezier(12, 21f, 8.2f, 19.6f, 4.8f, 16.4f, 4.8f, 11.4f);
        p.AddLine(4.8f, 11.4f, 4.8f, 6.2f);
        p.CloseFigure();
        return p;
    }
}
