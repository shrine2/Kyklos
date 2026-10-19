using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Kyklos.Interop;

namespace Kyklos.Ui;

/// <summary>
/// The small always-on-top Kyklos disc that can be dragged anywhere.
/// Dropping files on it opens the wheel at the drop point; clicking it opens the wheel
/// for the current Explorer selection. Position is remembered in settings.
/// </summary>
internal sealed class DropTargetForm : LayeredForm
{
    private const int BaseSize = 58;

    private int _size;
    private bool _hover;
    private bool _pressed;
    private bool _moved;
    private Point _grabOffset;

    public event Action<string[], Point>? FilesDropped;
    public event Action? Clicked;
    public event Action<Point>? PositionChanged;
    public event Action<Point>? ContextRequested;

    public DropTargetForm()
    {
        AllowDrop = true;
    }

    public void ShowAt(Point location)
    {
        var scale = NativeMethods.ScaleForPoint(location);
        _size = (int)Math.Round(BaseSize * scale);

        var area = Screen.FromPoint(location).WorkingArea;
        location = new Point(
            Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - _size)),
            Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - _size)));

        Bounds = new Rectangle(location, new Size(_size, _size));
        if (!Visible) Show();
        RenderNow();
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ContextRequested?.Invoke(new Point(Left + e.X, Top + e.Y));
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        _pressed = true;
        _moved = false;
        var cursor = NativeMethods.CursorPosition();
        _grabOffset = new Point(cursor.X - Left, cursor.Y - Top);
        Capture = true;
        SetHover(true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_pressed)
        {
            SetHover(ClientRectangle.Contains(e.Location));
            return;
        }

        var cursor = NativeMethods.CursorPosition();
        var target = new Point(cursor.X - _grabOffset.X, cursor.Y - _grabOffset.Y);

        if (Math.Abs(target.X - Left) > 2 || Math.Abs(target.Y - Top) > 2) _moved = true;
        MoveTo(target);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        Capture = false;
        var wasMoved = _moved;
        _pressed = false;
        _moved = false;

        if (wasMoved) PositionChanged?.Invoke(Location);
        else Clicked?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_pressed) SetHover(false);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        var files = ExtractFiles(e);
        e.Effect = files.Length == 0 ? DragDropEffects.None : DragDropEffects.Copy;
        SetHover(files.Length > 0);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        SetHover(false);
        var files = ExtractFiles(e);
        e.Effect = files.Length == 0 ? DragDropEffects.None : DragDropEffects.Copy;
        if (files.Length == 0) return;

        // The wheel opens at the drop point: convert where you are, no import step.
        FilesDropped?.Invoke(files, new Point(Left + e.X, Top + e.Y));
    }

    protected override void OnDragLeave(EventArgs e) => SetHover(false);

    private static string[] ExtractFiles(DragEventArgs e)
    {
        try
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return [];
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return [];
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void SetHover(bool value)
    {
        if (_hover == value) return;
        _hover = value;
        RenderNow();
    }

    // ---------------------------------------------------------------- rendering

    private void RenderNow()
    {
        if (!Visible || _size <= 0) return;

        using var bitmap = Render();
        Present(bitmap, Location);
    }

    private Bitmap Render()
    {
        var bitmap = new Bitmap(_size, _size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var inset = _size * 0.10f;
        var radius = _size / 2f - inset;
        var centre = _size / 2f;

        for (var i = 5; i >= 1; i--)
        {
            var shadowRadius = radius + i * 1.4f;
            using var shadow = new SolidBrush(Color.FromArgb(11, 0, 0, 0));
            g.FillEllipse(shadow, centre - shadowRadius, centre - shadowRadius, shadowRadius * 2, shadowRadius * 2);
        }

        var bounds = new RectangleF(centre - radius, centre - radius, radius * 2, radius * 2);
        using (var fill = new LinearGradientBrush(bounds,
                   _hover ? Theme.AccentSoft : Theme.Accent,
                   _hover ? Theme.Accent : Color.FromArgb(255, 214, 96, 32),
                   70f))
        {
            g.FillEllipse(fill, bounds);
        }

        using (var rim = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, _size * 0.02f)))
            g.DrawEllipse(rim, bounds);

        DrawDropGlyph(g, centre, radius);
        return bitmap;
    }

    /// <summary>A downward arrow onto a baseline — a universally readable "drop here" mark.</summary>
    private static void DrawDropGlyph(Graphics g, float centre, float radius)
    {
        var unit = radius / 10f;
        using var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));

        var stemWidth = unit * 2.0f;
        var stemTop = centre - unit * 4.6f;
        var stemBottom = centre + unit * 0.9f;
        g.FillRectangle(brush, centre - stemWidth / 2f, stemTop, stemWidth, stemBottom - stemTop);

        var headWidth = unit * 5.4f;
        var headTop = stemBottom - unit * 0.2f;
        var headBottom = centre + unit * 4.0f;
        g.FillPolygon(brush,
        [
            new PointF(centre - headWidth / 2f, headTop),
            new PointF(centre + headWidth / 2f, headTop),
            new PointF(centre, headBottom),
        ]);

        using var line = new Pen(Color.FromArgb(235, 255, 255, 255), unit * 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(line, centre - unit * 5.0f, centre + unit * 6.2f, centre + unit * 5.0f, centre + unit * 6.2f);
    }
}
