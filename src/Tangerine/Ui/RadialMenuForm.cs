using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tangerine.Core;
using Tangerine.Interop;

namespace Tangerine.Ui;

/// <summary>
/// The radial format wheel.
/// Two modes:
///  - interactive (hotkey / floating drop target): the window receives mouse events, clicking a
///    wedge commits, Escape or the hub cancels;
///  - drag-driven (files dragged over the drop surface): the window is click-through so the drag
///    keeps landing on the drop surface, and hover is driven externally from DragOver coordinates.
///    In that mode toggle tools arm after a short dwell so the cursor can travel across the inner
///    ring to reach the outer formats without accidental activation.
/// </summary>
internal sealed class RadialMenuForm : LayeredForm
{
    private const int DwellThresholdMs = 420;
    private const int TickIntervalMs = 30;

    private readonly List<MediaTarget> _targets = [];
    private readonly List<ToolItem> _tools = [];
    private readonly System.Windows.Forms.Timer _tick;

    private MediaKind _source = MediaKind.Unknown;
    private ConvertOptions _options = new();
    private string _caption = string.Empty;

    private float _scale = 1f;
    private int _radius;
    private int _pad;
    private Size _bitmapSize;
    private Point _discCenter;
    private Point _centerOnScreen;

    private float _hubR;
    private float _innerOuter;
    private float _outerInner;
    private float _outerOuter;

    private int _hoverTarget = -1;
    private int _hoverTool = -1;
    private bool _hoverHub;
    private int _dwellMs;

    public event Action<MediaTarget>? TargetChosen;
    public event Action<ToolId>? ToolRequested;
    public event Action<ToolId>? ToolToggled;
    public event Action? Cancelled;

    /// <summary>True when the wheel is driven by mouse input rather than an in-flight drag.</summary>
    public bool Interactive { get; private set; } = true;

    public ConvertOptions Options => _options;

    public RadialMenuForm()
    {
        _tick = new System.Windows.Forms.Timer { Interval = TickIntervalMs };
        _tick.Tick += (_, _) => OnTick();
        KeyPreview = true;
    }

    public IReadOnlyList<MediaTarget> Targets => _targets;

    // ---------------------------------------------------------------- lifecycle

    public void Configure(MediaKind source, IReadOnlyList<string> files, ConvertOptions options)
    {
        _source = source;
        _options = options;

        _targets.Clear();
        _targets.AddRange(FormatCatalog.TargetsFor(source));

        _caption = BuildCaption(files);
        ResetHover();
    }

    public void ShowWheel(Point anchor, bool interactive)
    {
        Interactive = interactive;
        RebuildTools();
        ResetHover();

        _scale = NativeMethods.ScaleForPoint(anchor);
        _radius = (int)Math.Round(148 * _scale);
        _pad = (int)Math.Round(20 * _scale);

        var captionHeight = (int)Math.Round((interactive ? 52 : 34) * _scale);
        _discCenter = new Point(_pad + _radius, _pad + _radius);
        _bitmapSize = new Size((_radius + _pad) * 2, (_radius + _pad) * 2 + captionHeight);

        _outerOuter = _radius;
        _hubR = _radius * 0.215f;
        if (_tools.Count > 0)
        {
            _innerOuter = _radius * 0.60f;
            _outerInner = _radius * 0.645f;
        }
        else
        {
            _innerOuter = _hubR;
            _outerInner = _hubR * 1.07f;
        }

        var area = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(anchor.X - _discCenter.X, area.Left, Math.Max(area.Left, area.Right - _bitmapSize.Width));
        var y = Math.Clamp(anchor.Y - _discCenter.Y, area.Top, Math.Max(area.Top, area.Bottom - _bitmapSize.Height));

        Bounds = new Rectangle(new Point(x, y), _bitmapSize);
        _centerOnScreen = new Point(x + _discCenter.X, y + _discCenter.Y);

        ApplyClickThrough(!interactive);

        if (!Visible) Show();
        ApplyClickThrough(!interactive); // re-assert after the handle exists
        RenderNow();

        _tick.Enabled = true;
    }

    public void HideWheel()
    {
        _tick.Enabled = false;
        if (!Visible) return;
        Visible = false;
    }

    private void RebuildTools()
    {
        _tools.Clear();
        _tools.AddRange(FormatCatalog.ToolsFor(_source, _options));

        // A trim/resize dialog cannot be answered mid-drag.
        if (!Interactive) _tools.RemoveAll(tool => tool.Id == ToolId.TrimResize);
    }

    /// <summary>Re-reads tool state after the caller changed the options.</summary>
    public void RefreshOptions(ConvertOptions options)
    {
        _options = options;
        RebuildTools();
        RenderNow();
    }

    private string BuildCaption(IReadOnlyList<string> files)
    {
        var count = files.Count;
        if (count == 0) return "nothing selected";
        if (count == 1) return Path.GetFileName(files[0]);
        return $"{count} files";
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Interactive) return;
        UpdateHover(ScreenPointFromClient(e.Location));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Interactive) return;

        if (e.Button == MouseButtons.Right)
        {
            RequestCancel();
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        UpdateHover(ScreenPointFromClient(e.Location));
        ActivateHover();
    }

    /// <summary>Called by the drop surface while a drag is in flight.</summary>
    public void DriveHover(Point screenPoint)
    {
        if (Interactive) return;
        UpdateHover(screenPoint);
    }

    /// <summary>Called by the drop surface when the user releases the mouse button.</summary>
    public bool DriveCommit()
    {
        if (Interactive) return false;
        if (_hoverHub)
        {
            RequestCancel();
            return false;
        }

        // A toggle in drag mode needs the dwell gesture; releasing on it is a no-op.
        if (_hoverTool >= 0 && _hoverTool < _tools.Count && _tools[_hoverTool].Toggle)
        {
            _dwellMs = 0;
            RenderNow();
            return false;
        }

        return ActivateHover();
    }

    private Point ScreenPointFromClient(Point client) => new(Location.X + client.X, Location.Y + client.Y);

    private void OnTick()
    {
        if (Interactive)
        {
            if (NativeMethods.IsKeyDown(NativeMethods.VK_ESCAPE)) RequestCancel();
            return;
        }

        // Drag mode: arm toggle tools only after the cursor rests on them.
        if (_hoverTool >= 0 && _hoverTool < _tools.Count && _tools[_hoverTool].Toggle)
        {
            _dwellMs += TickIntervalMs;
            if (_dwellMs >= DwellThresholdMs)
            {
                var toolId = _tools[_hoverTool].Id;
                _dwellMs = 0;
                ToolToggled?.Invoke(toolId);
                return;
            }
            RenderNow();
            return;
        }

        if (_dwellMs == 0) return;
        _dwellMs = 0;
        RenderNow();
    }

    private void RequestCancel()
    {
        HideWheel();
        Cancelled?.Invoke();
    }

    /// <summary>Commits whatever is under the cursor. Returns true when something was chosen.</summary>
    private bool ActivateHover()
    {
        if (_hoverHub)
        {
            RequestCancel();
            return false;
        }

        if (_hoverTool >= 0 && _hoverTool < _tools.Count)
        {
            var tool = _tools[_hoverTool];

            if (tool.Toggle)
            {
                ToolToggled?.Invoke(tool.Id);
                return false; // the wheel stays open so the user can pick a format next
            }

            HideWheel();
            ToolRequested?.Invoke(tool.Id);
            return true;
        }

        if (_hoverTarget >= 0 && _hoverTarget < _targets.Count)
        {
            var target = _targets[_hoverTarget];
            if (!target.Available)
            {
                // Keep the wheel open and let the caption explain what is missing.
                RenderNow();
                return false;
            }

            HideWheel();
            TargetChosen?.Invoke(target);
            return true;
        }

        return false;
    }

    private void UpdateHover(Point screenPoint)
    {
        var dx = screenPoint.X - _centerOnScreen.X;
        var dy = screenPoint.Y - _centerOnScreen.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var previousTarget = _hoverTarget;
        var previousTool = _hoverTool;
        var previousHub = _hoverHub;

        _hoverTarget = -1;
        _hoverTool = -1;
        _hoverHub = false;

        if (distance <= _hubR)
        {
            _hoverHub = true;
        }
        else if (distance <= _outerOuter)
        {
            var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            var wheelAngle = (angle + 90 + 360) % 360; // 0 = 12 o'clock, increasing clockwise

            if (_tools.Count > 0 && distance <= _innerOuter)
            {
                var sweep = 360.0 / _tools.Count;
                _hoverTool = Math.Clamp((int)(wheelAngle / sweep), 0, _tools.Count - 1);
            }
            else if (_targets.Count > 0 && distance >= _outerInner)
            {
                var sweep = 360.0 / _targets.Count;
                _hoverTarget = Math.Clamp((int)(wheelAngle / sweep), 0, _targets.Count - 1);
            }
        }

        if (previousTool != _hoverTool) _dwellMs = 0;

        if (previousTarget != _hoverTarget || previousTool != _hoverTool || previousHub != _hoverHub)
            RenderNow();
    }

    private void ResetHover()
    {
        _hoverTarget = -1;
        _hoverTool = -1;
        _hoverHub = false;
        _dwellMs = 0;
    }

    // ---------------------------------------------------------------- rendering

    private void RenderNow()
    {
        if (!Visible) return;

        using var bitmap = Render();
        Present(bitmap, Location);
    }

    private Bitmap Render()
    {
        var bitmap = new Bitmap(_bitmapSize.Width, _bitmapSize.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var cx = (float)_discCenter.X;
        var cy = (float)_discCenter.Y;

        DrawShadow(g, cx, cy);

        using (var disc = new LinearGradientBrush(
                   new RectangleF(cx - _outerOuter, cy - _outerOuter, _outerOuter * 2, _outerOuter * 2),
                   Theme.Disc1, Theme.Disc2, 65f))
        {
            g.FillEllipse(disc, cx - _outerOuter, cy - _outerOuter, _outerOuter * 2, _outerOuter * 2);
        }

        if (_tools.Count > 0)
        {
            using var innerDisc = new SolidBrush(Theme.Disc2);
            g.FillEllipse(innerDisc, cx - _innerOuter, cy - _innerOuter, _innerOuter * 2, _innerOuter * 2);
        }

        DrawRing(g, cx, cy, _outerInner, _outerOuter, _targets.Count, _hoverTarget,
            index => _targets[index].Label,
            index => _targets[index].Available,
            isToolRing: false,
            labelSize: _radius * 0.125f);

        DrawRing(g, cx, cy, _hubR * 1.04f, _innerOuter, _tools.Count, _hoverTool,
            index => _tools[index].Label,
            _ => true,
            isToolRing: true,
            labelSize: _radius * 0.082f);

        DrawDwellProgress(g, cx, cy);

        using (var rim = new Pen(Color.FromArgb(52, 255, 255, 255), Math.Max(1f, 1.1f * _scale)))
        {
            g.DrawEllipse(rim, cx - _outerOuter, cy - _outerOuter, _outerOuter * 2, _outerOuter * 2);
        }

        DrawHub(g, cx, cy);
        DrawCaption(g, cx);

        return bitmap;
    }

    private void DrawShadow(Graphics g, float cx, float cy)
    {
        for (var i = 9; i >= 1; i--)
        {
            var radius = _outerOuter + i * 1.7f * _scale;
            using var brush = new SolidBrush(Color.FromArgb(9, 0, 0, 0));
            g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
        }
    }

    private void DrawRing(
        Graphics g, float cx, float cy, float innerRadius, float outerRadius, int count, int hovered,
        Func<int, string> label, Func<int, bool> enabled, bool isToolRing, float labelSize)
    {
        if (count == 0) return;

        var sweep = 360f / count;
        var middleRadius = (innerRadius + outerRadius) / 2f;
        var bounds = new RectangleF(cx - outerRadius, cy - outerRadius, outerRadius * 2, outerRadius * 2);

        for (var i = 0; i < count; i++)
        {
            var start = -90f + i * sweep;
            using var path = WedgePath(cx, cy, innerRadius, outerRadius, start, sweep);

            if (i == hovered && enabled(i))
            {
                using var highlight = new LinearGradientBrush(bounds, Theme.AccentSoft, Theme.Accent, 70f);
                g.FillPath(highlight, path);
            }
            else
            {
                var color = !enabled(i) ? Theme.WedgeDisabled : (isToolRing ? Theme.WedgeTool : Theme.Wedge);
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);
            }

            using var separator = new Pen(Color.FromArgb(70, 0, 0, 0), Math.Max(1f, _scale));
            g.DrawPath(separator, path);
        }

        for (var i = 0; i < count; i++)
        {
            var text = label(i);
            if (string.IsNullOrEmpty(text)) continue;

            var midAngle = (-90f + i * sweep + sweep / 2f) * MathF.PI / 180f;
            var point = new PointF(cx + middleRadius * MathF.Cos(midAngle), cy + middleRadius * MathF.Sin(midAngle));

            // Shrink the label so it never spills into a neighbouring wedge.
            var chord = 2f * middleRadius * MathF.Sin(sweep * MathF.PI / 360f) * 0.84f;
            using var font = FitFont(g, text, labelSize, chord);

            var color = !enabled(i) ? Theme.Dim : (i == hovered ? Color.White : Theme.Text);
            Theme.DrawShadowed(g, text, font, color, point);
        }
    }

    private void DrawDwellProgress(Graphics g, float cx, float cy)
    {
        if (Interactive || _dwellMs <= 0) return;
        if (_hoverTool < 0 || _hoverTool >= _tools.Count || !_tools[_hoverTool].Toggle) return;

        var sweep = 360f / _tools.Count;
        var start = -90f + _hoverTool * sweep;
        var progress = Math.Min(1f, _dwellMs / (float)DwellThresholdMs);
        var middleRadius = (_hubR * 1.04f + _innerOuter) / 2f;

        using var pen = new Pen(Theme.AccentSoft, Math.Max(2f, _radius * 0.024f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        g.DrawArc(pen,
            new RectangleF(cx - middleRadius, cy - middleRadius, middleRadius * 2, middleRadius * 2),
            start + 3f, (sweep - 6f) * progress);
    }

    private void DrawHub(Graphics g, float cx, float cy)
    {
        var radius = _hubR;
        using (var brush = new SolidBrush(_hoverHub ? Theme.Accent : Color.FromArgb(255, 22, 22, 27)))
            g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);

        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, _scale)))
            g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);

        var text = _hoverHub ? "✕" : KindLabel(_source);
        using var font = Theme.Font(Math.Max(8f, radius * (_hoverHub ? 0.62f : 0.46f)), FontStyle.Bold);
        Theme.DrawShadowed(g, text, font, _hoverHub ? Color.White : Theme.Muted, new PointF(cx, cy));
    }

    private void DrawCaption(Graphics g, float cx)
    {
        var top = _pad * 2f + _outerOuter * 2f + 6f * _scale;

        var headline = HoverDescription();
        using (var font = Theme.Font(Math.Max(10f, 14.5f * _scale), FontStyle.Bold))
            Theme.DrawShadowed(g, headline, font, Theme.Text, new PointF(cx, top + 10f * _scale));

        if (!Interactive) return;

        using var subFont = Theme.Font(Math.Max(9f, 11.5f * _scale));
        Theme.DrawShadowed(g, _options.Describe(), subFont, Theme.Muted, new PointF(cx, top + 29f * _scale));
    }

    private string HoverDescription()
    {
        if (_hoverTarget >= 0 && _hoverTarget < _targets.Count)
        {
            var target = _targets[_hoverTarget];
            if (!target.Available) return $"{target.Label} — {target.UnavailableReason}";
            return target.Hint is { Length: > 0 } hint ? $"{target.Label} · {hint}" : target.Label;
        }

        if (_hoverTool >= 0 && _hoverTool < _tools.Count)
        {
            var tool = _tools[_hoverTool];
            return tool.Toggle
                ? (Interactive ? $"{tool.Label.Replace(" ✓", "")} (click to toggle)" : $"{tool.Label.Replace(" ✓", "")} (hold to toggle)")
                : tool.Label.Replace(" ✓", "");
        }

        if (_hoverHub) return Interactive ? "Cancel" : "Release here to cancel";

        return Interactive ? _caption : $"{_caption} — release on a format";
    }

    private static string KindLabel(MediaKind kind) => kind switch
    {
        MediaKind.Video => "VID",
        MediaKind.Audio => "AUD",
        MediaKind.Image => "IMG",
        MediaKind.Pdf => "PDF",
        MediaKind.Document => "DOC",
        _ => "•••",
    };

    private static Font FitFont(Graphics g, string text, float preferredSize, float maxWidth)
    {
        var size = preferredSize;
        while (size > 7.5f)
        {
            var candidate = Theme.Font(size, FontStyle.Bold);
            if (g.MeasureString(text, candidate).Width <= maxWidth) return candidate;
            candidate.Dispose();
            size -= 0.6f;
        }
        return Theme.Font(7.5f, FontStyle.Bold);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Stop();
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }
}
