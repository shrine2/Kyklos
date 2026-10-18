using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Kyklos.Ui;

/// <summary>
/// The full-screen surface that makes Shift/ctrl+drag work on Windows.
/// Explorer owns its own drag loop, so a wheel cannot be injected into it — instead
/// this window is armed whenever the user begins a pointer drag with the trigger
/// modifiers down, and becomes the topmost OLE drop target. Drag events arrive here
/// and are forwarded to the radial wheel, which is positioned at the drag origin.
/// The window is nearly invisible (alpha 2) so the drag experience is undisturbed,
/// but non-zero alpha still lets Windows hit-test it for drops.
/// </summary>
internal sealed class DropSurfaceForm : LayeredForm
{
    private const byte ActiveFillAlpha = 3;

    private Rectangle _virtualBounds;
    private Bitmap? _idleBitmap;
    private Bitmap? _activeBitmap;
    private bool _active;

    public event Action<string[], Point>? FilesEntered;
    public event Action<Point>? PointerMoved;
    public event Action<string[], Point>? FilesDropped;
    public event Action? DragLeft;

    public DropSurfaceForm()
    {
        AllowDrop = true;
    }

    public bool IsArmed { get; private set; }

    public void Arm()
    {
        if (IsArmed) return;

        _virtualBounds = SystemInformation.VirtualScreen;
        Bounds = _virtualBounds;
        BuildBitmaps();

        _active = false;
        if (!Visible) Show();
        Present(_idleBitmap!, _virtualBounds.Location);
        IsArmed = true;
    }

    public void Disarm()
    {
        if (!IsArmed && !Visible) return;

        IsArmed = false;
        _active = false;
        if (Visible) Visible = false;
        DisposeBitmaps();
    }

    private void EnsureActiveLayer()
    {
        if (_active || _activeBitmap is null) return;
        _active = true;
        Present(_activeBitmap, _virtualBounds.Location);
    }

    // ---------------------------------------------------------------- drag events

    protected override void OnDragEnter(DragEventArgs e)
    {
        var files = ExtractFiles(e);
        if (files.Length == 0)
        {
            e.Effect = DragDropEffects.None;
            DragLeft?.Invoke();
            return;
        }

        e.Effect = DragDropEffects.Copy;
        EnsureActiveLayer();
        FilesEntered?.Invoke(files, ScreenFromClient(e.X, e.Y));
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effect = DragDropEffects.Copy;
        PointerMoved?.Invoke(ScreenFromClient(e.X, e.Y));
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        var files = ExtractFiles(e);
        e.Effect = files.Length == 0 ? DragDropEffects.None : DragDropEffects.Copy;
        if (files.Length > 0) FilesDropped?.Invoke(files, ScreenFromClient(e.X, e.Y));
    }

    protected override void OnDragLeave(EventArgs e) => DragLeft?.Invoke();

    private Point ScreenFromClient(int x, int y) => new(_virtualBounds.Left + x, _virtualBounds.Top + y);

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

    // ---------------------------------------------------------------- rendering

    private void BuildBitmaps()
    {
        DisposeBitmaps();

        var size = _virtualBounds.Size;
        _idleBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        _activeBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);

        using (var g = Graphics.FromImage(_idleBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var veil = new SolidBrush(Color.FromArgb(18, 0, 0, 0)))
                g.FillRectangle(veil, 0, 0, size.Width, size.Height);

            foreach (var screen in Screen.AllScreens)
            {
                var rect = ToBitmapRect(screen);
                using var pen = new Pen(Color.FromArgb(95, Theme.Accent), 2f);
                g.DrawRectangle(pen, rect);

                var centerX = rect.Left + rect.Width / 2f;
                using var title = Theme.Font(15f, FontStyle.Bold);
                using var subtitle = Theme.Font(12f);
                Theme.DrawShadowed(g, "Kyklos — drag a file, release on a format", title, Theme.Text,
                    new PointF(centerX, rect.Top + 36f));
                Theme.DrawShadowed(g, "release over the centre to cancel", subtitle, Theme.Muted,
                    new PointF(centerX, rect.Top + 58f));
            }
        }

        using (var g = Graphics.FromImage(_activeBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Alpha must stay above zero or Windows stops hit-testing this window for drops.
            using (var fill = new SolidBrush(Color.FromArgb(ActiveFillAlpha, 0, 0, 0)))
                g.FillRectangle(fill, 0, 0, size.Width, size.Height);

            foreach (var screen in Screen.AllScreens)
            {
                using var pen = new Pen(Color.FromArgb(45, Theme.Accent), 2f);
                g.DrawRectangle(pen, ToBitmapRect(screen));
            }
        }
    }

    private Rectangle ToBitmapRect(Screen screen) => new(
        screen.Bounds.Left - _virtualBounds.Left,
        screen.Bounds.Top - _virtualBounds.Top,
        screen.Bounds.Width,
        screen.Bounds.Height);

    private void DisposeBitmaps()
    {
        _idleBitmap?.Dispose();
        _idleBitmap = null;
        _activeBitmap?.Dispose();
        _activeBitmap = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeBitmaps();
        base.Dispose(disposing);
    }
}
