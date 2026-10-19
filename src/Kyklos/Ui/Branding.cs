using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Kyklos.Interop;

namespace Kyklos.Ui;

/// <summary>
/// The Kyklos mark: a dark disc carrying the radial wheel, an accent ring around it, and a
/// glowing hub at the centre. That is the photograph the icon was asked for — a bright core
/// with dark lanes radiating out of it — flattened into the app's own palette, so the icon
/// says what the program does rather than merely decorating it.
///
/// The shipped .ico is not built during compilation; it is written once by
/// <c>Kyklos.exe --writeicon &lt;path&gt;</c> and committed. This drawing stays the single
/// source of truth, and the file can be re-rendered at any time.
/// </summary>
internal static class Branding
{
    /// <summary>
    /// The sizes Windows asks for. The small ones are drawn at their own size rather than
    /// scaled down, because a 256-pixel mark scaled to 16 pixels loses its ring.
    /// </summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private const int Lanes = 8;

    public static void DrawMark(Graphics g, float size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var c = size / 2f;
        var disc = size * 0.5f - size * 0.02f;
        var bounds = new RectangleF(c - disc, c - disc, disc * 2, disc * 2);

        // The wheel's own disc.
        using (var fill = new LinearGradientBrush(bounds, Theme.Disc1, Theme.Disc2, 90f))
            g.FillEllipse(fill, bounds);

        // Alternating wedges, so the disc reads as a divided wheel rather than a plain circle.
        var sweep = 360f / Lanes;
        using (var wedge = new SolidBrush(Color.FromArgb(120, Theme.Wedge)))
        {
            for (var i = 0; i < Lanes; i += 2)
                g.FillPie(wedge, bounds.X, bounds.Y, bounds.Width, bounds.Height, -90f + i * sweep, sweep);
        }

        // The lanes between the wedges.
        var laneWidth = Math.Max(size * 0.017f, 1f);
        using (var lane = new Pen(Color.FromArgb(180, 9, 9, 12), laneWidth))
        {
            for (var i = 0; i < Lanes; i++)
            {
                var angle = (float)(i * 2 * Math.PI / Lanes - Math.PI / 2);
                var inner = disc * 0.36f;
                var outer = disc * 0.99f;
                g.DrawLine(lane,
                    c + inner * MathF.Cos(angle), c + inner * MathF.Sin(angle),
                    c + outer * MathF.Cos(angle), c + outer * MathF.Sin(angle));
            }
        }

        // The ring, matching the floating icon on the desktop.
        var ringWidth = Math.Max(size * 0.082f, 1.3f);
        var ringRadius = disc - ringWidth / 2f;
        using (var ring = new LinearGradientBrush(
                   new RectangleF(0, 0, size, size), Theme.AccentSoft, Theme.Accent, 60f))
        using (var pen = new Pen(ring, ringWidth))
            g.DrawEllipse(pen, c - ringRadius, c - ringRadius, ringRadius * 2, ringRadius * 2);

        // The hub: a soft accent halo with a bright core inside it. The halo is a smooth radial
        // brush rather than stacked rings, which band visibly at icon size.
        var hub = disc * 0.26f;
        var glow = hub * 2.05f;
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(c - glow, c - glow, glow * 2, glow * 2);
            using var halo = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(140, Theme.Accent),
                SurroundColors = [Color.FromArgb(0, Theme.Accent)],
            };
            g.FillEllipse(halo, c - glow, c - glow, glow * 2, glow * 2);
        }

        using (var core = new LinearGradientBrush(
                   new RectangleF(c - hub, c - hub, hub * 2, hub * 2), Theme.AccentSoft, Theme.Accent, 90f))
            g.FillEllipse(core, c - hub, c - hub, hub * 2, hub * 2);

        var ember = hub * 0.52f;
        using (var hot = new SolidBrush(Color.FromArgb(246, 255, 244, 226)))
            g.FillEllipse(hot, c - ember, c - ember, ember * 2, ember * 2);
    }

    public static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        DrawMark(g, size);
        return bitmap;
    }

    /// <summary>An <see cref="Icon"/> for window chrome, drawn rather than re-read from the .ico.</summary>
    public static Icon IconFor(int size)
    {
        using var bitmap = Render(size);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public static void WriteIco(string path, IEnumerable<int> sizes)
    {
        var frames = sizes.Distinct().OrderBy(s => s).Select(RenderFrame).ToArray();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                 // reserved
        writer.Write((ushort)1);                 // 1 = icon
        writer.Write((ushort)frames.Length);

        var offset = 6 + frames.Length * 16;
        foreach (var (size, data) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);               // palette size
            writer.Write((byte)0);               // reserved
            writer.Write((ushort)1);             // colour planes
            writer.Write((ushort)32);            // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in frames) writer.Write(data);
    }

    /// <summary>
    /// One icon frame, always as a raw bitmap. A PNG frame would be smaller, but GDI+ — and so
    /// a great deal of software that reads icons — cannot parse a PNG-compressed frame at all,
    /// while every consumer understands a plain 32-bit bitmap. The size is worth the reach.
    /// </summary>
    private static (int Size, byte[] Data) RenderFrame(int size)
    {
        using var bitmap = Render(size);
        using var memory = new MemoryStream();
        WriteBitmapFrame(memory, bitmap);
        return (size, memory.ToArray());
    }

    private static void WriteBitmapFrame(Stream target, Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;

        // Bottom-up BGRA, with a fully transparent AND mask: the alpha channel does the work.
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var colour = bitmap.GetPixel(x, height - 1 - y);
                var at = (y * width + x) * 4;
                pixels[at + 0] = colour.B;
                pixels[at + 1] = colour.G;
                pixels[at + 2] = colour.R;
                pixels[at + 3] = colour.A;
            }
        }

        var maskStride = (width + 31) / 32 * 4;
        var mask = new byte[maskStride * height];

        using var writer = new BinaryWriter(target);
        writer.Write(40);                        // BITMAPINFOHEADER
        writer.Write(width);
        writer.Write(height * 2);                // XOR image plus mask
        writer.Write((ushort)1);                 // planes
        writer.Write((ushort)32);                // bits per pixel
        writer.Write(0);                         // BI_RGB
        writer.Write(pixels.Length);             // image size
        writer.Write(0);                         // x pixels per metre
        writer.Write(0);                         // y pixels per metre
        writer.Write(0);                         // colours used
        writer.Write(0);                         // important colours
        writer.Write(pixels);
        writer.Write(mask);
    }
}
