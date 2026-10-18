namespace Kyklos.Ui;

/// <summary>Shared palette and font factory for every Kyklos surface.</summary>
internal static class Theme
{
    public static readonly Color Accent = Color.FromArgb(255, 255, 138, 61);
    public static readonly Color AccentSoft = Color.FromArgb(255, 255, 184, 112);
    public static readonly Color Disc1 = Color.FromArgb(255, 30, 30, 36);
    public static readonly Color Disc2 = Color.FromArgb(255, 16, 16, 20);
    public static readonly Color Wedge = Color.FromArgb(255, 46, 46, 55);
    public static readonly Color WedgeTool = Color.FromArgb(255, 36, 36, 44);
    public static readonly Color WedgeDisabled = Color.FromArgb(255, 28, 28, 33);
    public static readonly Color Text = Color.FromArgb(255, 244, 244, 250);
    public static readonly Color Muted = Color.FromArgb(255, 158, 160, 174);
    public static readonly Color Dim = Color.FromArgb(255, 96, 98, 110);
    public static readonly Color Error = Color.FromArgb(255, 242, 104, 104);
    public static readonly Color Ok = Color.FromArgb(255, 112, 202, 132);

    public static Font Font(float pixelSize, FontStyle style = FontStyle.Regular)
        => new("Segoe UI", pixelSize, style, GraphicsUnit.Pixel);

    /// <summary>Draws text with a dark offset copy so it stays readable over any wallpaper.</summary>
    public static void DrawShadowed(Graphics g, string text, Font font, Color color, PointF center)
    {
        var size = g.MeasureString(text, font);
        var origin = new PointF(center.X - size.Width / 2f, center.Y - size.Height / 2f);
        using (var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            g.DrawString(text, font, shadow, origin.X + 1f, origin.Y + 1f);
        using (var brush = new SolidBrush(color))
            g.DrawString(text, font, brush, origin);
    }
}
