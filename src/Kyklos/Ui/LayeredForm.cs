using Kyklos.Interop;

using System.Drawing.Drawing2D;

namespace Kyklos.Ui;

/// <summary>
/// Base class for windows rendered with per-pixel alpha (UpdateLayeredWindow).
/// Windows Forms has no built-in support for this, which is why every visual surface
/// in Kyklos derives from here.
/// </summary>
internal abstract class LayeredForm : Form
{
    protected LayeredForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        MinimizeBox = false;
        MaximizeBox = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>Never take focus — the wheel must not disturb the foreground app.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>When true the window is invisible to hit-testing, so drags fall through to the layer beneath.</summary>
    protected bool ClickThrough { get; private set; }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= NativeMethods.WS_EX_LAYERED
                                  | NativeMethods.WS_EX_NOACTIVATE
                                  | NativeMethods.WS_EX_TOOLWINDOW;
            if (ClickThrough) parameters.ExStyle |= NativeMethods.WS_EX_TRANSPARENT;
            return parameters;
        }
    }

    public void ApplyClickThrough(bool clickThrough)
    {
        if (ClickThrough == clickThrough) return;
        ClickThrough = clickThrough;

        var handle = Handle; // forces handle creation
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        style = clickThrough
            ? style | NativeMethods.WS_EX_TRANSPARENT
            : style & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, style);
    }

    // Painting is fully manual; WinForms must never touch the layered surface.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    /// <summary>
    /// Pushes a premultiplied-ARGB bitmap to the window at the given screen position.
    /// <paramref name="alpha"/> scales the whole surface, which is how fades are done.
    /// </summary>
    protected void Present(Bitmap bitmap, Point screenLocation, int alpha = 255)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            // GetHbitmap with a transparent background colour yields a 32bpp DIB that
            // UpdateLayeredWindow can consume together with AC_SRC_ALPHA.
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = NativeMethods.SelectObject(memoryDc, hBitmap);

            var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
            var source = new NativeMethods.POINT(0, 0);
            var destination = new NativeMethods.POINT(screenLocation.X, screenLocation.Y);
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = (byte)Math.Clamp(alpha, 0, 255),
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(
                Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (previous != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Moves an already-presented layered window without re-rendering it.</summary>
    protected void MoveTo(Point screenLocation)
    {
        Bounds = new Rectangle(screenLocation, Bounds.Size);
        NativeMethods.SetWindowPos(
            Handle, NativeMethods.HWND_TOPMOST, screenLocation.X, screenLocation.Y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    protected static GraphicsPath WedgePath(float cx, float cy, float innerRadius, float outerRadius, float startDegrees, float sweepDegrees)
    {
        var path = new GraphicsPath();

        if (sweepDegrees >= 359.9f)
        {
            path.AddEllipse(cx - outerRadius, cy - outerRadius, outerRadius * 2, outerRadius * 2);
            return path;
        }

        var start = startDegrees * MathF.PI / 180f;
        var end = (startDegrees + sweepDegrees) * MathF.PI / 180f;

        var innerStart = new PointF(cx + innerRadius * MathF.Cos(start), cy + innerRadius * MathF.Sin(start));
        var outerStart = new PointF(cx + outerRadius * MathF.Cos(start), cy + outerRadius * MathF.Sin(start));
        var outerEnd = new PointF(cx + outerRadius * MathF.Cos(end), cy + outerRadius * MathF.Sin(end));
        var innerEnd = new PointF(cx + innerRadius * MathF.Cos(end), cy + innerRadius * MathF.Sin(end));

        path.AddLine(innerStart, outerStart);
        path.AddArc(cx - outerRadius, cy - outerRadius, outerRadius * 2, outerRadius * 2, startDegrees, sweepDegrees);
        path.AddLine(outerEnd, innerEnd);
        path.AddArc(cx - innerRadius, cy - innerRadius, innerRadius * 2, innerRadius * 2, startDegrees + sweepDegrees, -sweepDegrees);
        path.CloseFigure();
        return path;
    }

    /// <summary>A stadium/lozenge shape: a rectangle with semicircular ends.</summary>
    protected static GraphicsPath PillPath(RectangleF bounds)
    {
        var radius = bounds.Height / 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, radius * 2, radius * 2, 90, 180);
        path.AddArc(bounds.Right - radius * 2, bounds.Top, radius * 2, radius * 2, 270, 180);
        path.CloseFigure();
        return path;
    }
}
