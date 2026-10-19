using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Kyklos.Core;
using Kyklos.Interop;

namespace Kyklos.Ui;

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
    private long _sourceBytes;
    private int _sourceCount;

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

    // Busy mode: a conversion is running, so the hub becomes a spinner that doubles as cancel.
    private bool _busy;
    private bool _fading;
    private float _spinDegrees;
    private string _busyCaption = string.Empty;
    private string _busyDetail = string.Empty;
    private int _fadeAlpha = 255;

    public event Action<MediaTarget>? TargetChosen;
    public event Action<ToolId>? ToolRequested;
    public event Action<ToolId>? ToolToggled;
    public event Action? Cancelled;
    public event Action? CancelRequested;

    /// <summary>True when the wheel is driven by mouse input rather than an in-flight drag.</summary>
    public bool Interactive { get; private set; } = true;

    /// <summary>True while a conversion is running; the wheel stays open as a spinner.</summary>
    public bool Busy => _busy;

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
        _targets.AddRange(FormatCatalog.TargetsFor(source, files.FirstOrDefault()));

        _caption = BuildCaption(files);
        _sourceCount = files.Count;
        _sourceBytes = SumBytes(files);
        ResetHover();
    }

    private static long SumBytes(IReadOnlyList<string> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            try { total += new FileInfo(file).Length; } catch { /* unreadable entries just do not count */ }
        }
        return total;
    }

    public void ShowWheel(Point anchor, bool interactive)
    {
        Interactive = interactive;

        // Safety: a fresh wheel must never inherit a previous job's spinner.
        _busy = false;
        _fading = false;
        _fadeAlpha = 255;

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
        _busy = false;
        _fading = false;
        _fadeAlpha = 255;
        ResetHover();
        if (!Visible) return;
        Visible = false;
    }

    // ---------------------------------------------------------------- busy mode

    /// <summary>
    /// Switches the wheel into its running state: the hub becomes a spinner and the wheel
    /// ignores format picking until the work finishes. Clicks on the hub request a cancel.
    /// </summary>
    public void BeginBusy(string caption)
    {
        _busy = true;
        _fading = false;
        _fadeAlpha = 255;
        _spinDegrees = 0;
        _busyCaption = caption;
        _busyDetail = string.Empty;
        ResetHover();

        // Note: the click-through state is deliberately left alone. In drag-driven mode the
        // wheel must stay click-through so a long job cannot swallow clicks on the desktop
        // underneath; cancel is available there through Escape instead, which is polled
        // globally. Interactive wheels are already clickable, so the hub works as a button.
        if (!Visible) Show();
        _tick.Enabled = true;
        RenderNow();
    }

    /// <summary>Updates the caption line while work is in progress.</summary>
    public void UpdateBusy(string caption, string detail)
    {
        if (!_busy) return;
        _busyCaption = caption;
        _busyDetail = detail;
        RenderNow();
    }

    /// <summary>Fades the wheel out and hides it once the work is done.</summary>
    public void EndBusy()
    {
        if (!_busy) return;
        _fading = true;
        _spinDegrees = 0;
        _tick.Enabled = true;
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

    protected override void OnMouseLeave(EventArgs e)
    {
        // Without this the highlight (and the busy cancel glyph) would stick on screen after
        // the pointer left the wheel, since hover is only recomputed from mouse moves inside it.
        base.OnMouseLeave(e);
        if (!Interactive) return;
        ResetHover();
        RenderNow();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Interactive) return;

        if (e.Button == MouseButtons.Right)
        {
            // While running, right-click asks the job to stop rather than tearing the wheel
            // down — the wheel has to stay up to report the outcome.
            if (_busy) CancelRequested?.Invoke();
            else RequestCancel();
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        UpdateHover(ScreenPointFromClient(e.Location));

        // While a conversion runs the hub is the only live control, and it means cancel.
        if (_busy)
        {
            if (_hoverHub) CancelRequested?.Invoke();
            return;
        }

        ActivateHover();
    }

    /// <summary>Called by the drop surface while a drag is in flight.</summary>
    public void DriveHover(Point screenPoint)
    {
        if (Interactive || _busy) return;
        UpdateHover(screenPoint);
    }

    /// <summary>Called by the drop surface when the user releases the mouse button.</summary>
    public bool DriveCommit()
    {
        if (Interactive || _busy) return false;
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
        if (_fading)
        {
            _fadeAlpha -= 45;
            if (_fadeAlpha <= 0)
            {
                _fading = false;
                _fadeAlpha = 255;
                _busy = false;
                _tick.Enabled = false;
                ResetHover();
                Visible = false;
                ApplyClickThrough(!Interactive);
                return;
            }

            RenderNow();
            return;
        }

        if (_busy)
        {
            if (NativeMethods.IsKeyDown(NativeMethods.VK_ESCAPE))
            {
                CancelRequested?.Invoke();
                return;
            }

            _spinDegrees = (_spinDegrees + 9f) % 360f;
            RenderNow();
            return;
        }

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

            // The wheel is left up on purpose: the owner either starts a job (and the wheel
            // becomes a spinner) or closes it itself.
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

            // As above: the owner decides whether this becomes a running job or a plain close.
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

        // While running, only the hub is meaningful, and only as a cancel target.
        if (_busy)
        {
            _hoverHub = distance <= _hubR;
            if (previousHub != _hoverHub) RenderNow();
            return;
        }

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
                _hoverTool = Math.Clamp((int)(((wheelAngle + sweep / 2) % 360) / sweep), 0, _tools.Count - 1);
            }
            else if (_targets.Count > 0 && distance >= _outerInner)
            {
                var sweep = 360.0 / _targets.Count;
                _hoverTarget = Math.Clamp((int)(((wheelAngle + sweep / 2) % 360) / sweep), 0, _targets.Count - 1);
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
        Present(bitmap, Location, _fadeAlpha);
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
            labelSize: _radius * 0.082f,
            switchedOn: index => _tools[index].Toggle && _tools[index].Active);

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
        Func<int, string> label, Func<int, bool> enabled, bool isToolRing, float labelSize,
        Func<int, bool>? switchedOn = null)
    {
        if (count == 0) return;

        var sweep = 360f / count;
        var middleRadius = (innerRadius + outerRadius) / 2f;
        var bounds = new RectangleF(cx - outerRadius, cy - outerRadius, outerRadius * 2, outerRadius * 2);

        for (var i = 0; i < count; i++)
        {
            var start = WedgeStart(i, sweep);
            using var path = WedgePath(cx, cy, innerRadius, outerRadius, start, sweep);

            if (i == hovered && enabled(i))
            {
                using var highlight = new LinearGradientBrush(bounds, Theme.AccentSoft, Theme.Accent, 70f);
                g.FillPath(highlight, path);
            }
            else if (switchedOn is not null && switchedOn(i))
            {
                // A tool that is switched on is tinted rather than flagged with a symbol, so the
                // wedges stay readable as plain words.
                using var tint = new LinearGradientBrush(
                    bounds,
                    Color.FromArgb(140, Theme.AccentSoft),
                    Color.FromArgb(155, Theme.Accent),
                    70f);
                g.FillPath(tint, path);
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

            var midAngle = (-90f + i * sweep) * MathF.PI / 180f;
            var point = new PointF(cx + middleRadius * MathF.Cos(midAngle), cy + middleRadius * MathF.Sin(midAngle));

            // Shrink the label so it never spills into a neighbouring wedge.
            var chord = 2f * middleRadius * MathF.Sin(sweep * MathF.PI / 360f) * 0.84f;
            using var font = FitFont(g, text, labelSize, chord);

            var lit = i == hovered || (switchedOn is not null && switchedOn(i));
            var color = !enabled(i) ? Theme.Dim : (lit ? Color.White : Theme.Text);
            Theme.DrawShadowed(g, text, font, color, point);
        }
    }

    private void DrawDwellProgress(Graphics g, float cx, float cy)
    {
        if (Interactive || _dwellMs <= 0) return;
        if (_hoverTool < 0 || _hoverTool >= _tools.Count || !_tools[_hoverTool].Toggle) return;

        var sweep = 360f / _tools.Count;
        var start = WedgeStart(_hoverTool, sweep);
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

        // The hub only turns accent when it means "cancel" *outside* a running job; while one
        // is running it stays dark so the spinner arc on top of it keeps its contrast.
        var hubFill = _hoverHub && !_busy
            ? Theme.Accent
            : _hoverHub ? Color.FromArgb(255, 40, 40, 49) : Color.FromArgb(255, 22, 22, 27);

        using (var brush = new SolidBrush(hubFill))
            g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);

        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, _scale)))
            g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);

        if (_busy)
        {
            // The spinner is the primary reading of this state, so it is drawn even when the
            // cursor is parked on the hub (which it is right after a drag-and-drop commit).
            var ring = radius * 0.66f;
            var thickness = Math.Max(2f, radius * 0.15f);

            using (var track = new Pen(Color.FromArgb(44, 255, 255, 255), thickness))
                g.DrawEllipse(track, cx - ring, cy - ring, ring * 2, ring * 2);

            using (var arc = new Pen(Theme.Accent, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(arc, cx - ring, cy - ring, ring * 2, ring * 2, _spinDegrees, 260f);

            if (_hoverHub)
            {
                using var cancelFont = Theme.Font(Math.Max(7f, radius * 0.5f), FontStyle.Bold);
                Theme.DrawShadowed(g, "✕", cancelFont, Color.White, new PointF(cx, cy));
            }

            return;
        }

        if (_hoverHub)
        {
            using var cancelFont = Theme.Font(Math.Max(8f, radius * 0.62f), FontStyle.Bold);
            Theme.DrawShadowed(g, "✕", cancelFont, Color.White, new PointF(cx, cy));
            return;
        }

        // The macOS app puts the size of the selection in the hub while a format is being
        // chosen. The pill is allowed to run a little wider than the disc.
        if (_sourceBytes > 0)
        {
            using var font = Theme.Font(Math.Max(8f, radius * 0.42f), FontStyle.Bold);
            var text = _sourceCount > 1 ? $"{_sourceCount} files" : Paths.HumanSize(_sourceBytes);
            var measured = g.MeasureString(text, font);

            var bounds = new RectangleF(
                cx - measured.Width / 2f - radius * 0.30f,
                cy - measured.Height / 2f,
                measured.Width + radius * 0.60f,
                measured.Height);

            using var pill = PillPath(bounds);
            using (var fill = new SolidBrush(Color.FromArgb(234, 255, 253, 245)))
                g.FillPath(fill, pill);
            using (var rim = new Pen(Color.FromArgb(56, 0, 0, 0), Math.Max(1f, _scale)))
                g.DrawPath(rim, pill);

            using var textBrush = new SolidBrush(Color.FromArgb(255, 28, 26, 24));
            using var centred = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(text, font, textBrush, bounds, centred);
            return;
        }

        using var kindFont = Theme.Font(Math.Max(8f, radius * 0.46f), FontStyle.Bold);
        Theme.DrawShadowed(g, KindLabel(_source), kindFont, Theme.Muted, new PointF(cx, cy));
    }

    private void DrawCaption(Graphics g, float cx)
    {
        var top = _pad * 2f + _outerOuter * 2f + 6f * _scale;

        if (_busy)
        {
            using (var busyFont = Theme.Font(Math.Max(10f, 14.5f * _scale), FontStyle.Bold))
                Theme.DrawShadowed(g, _busyCaption, busyFont, Theme.Text, new PointF(cx, top + 10f * _scale));

            if (_busyDetail.Length > 0)
            {
                using var detailFont = Theme.Font(Math.Max(9f, 11.5f * _scale));
                Theme.DrawShadowed(g, Shorten(_busyDetail, 34), detailFont, Theme.Muted, new PointF(cx, top + 29f * _scale));
            }

            return;
        }

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
            if (!target.Available) return $"{target.Label} (unavailable, {target.UnavailableReason})";
            return target.Hint is { Length: > 0 } hint ? $"{target.Label} ({hint})" : target.Label;
        }

        if (_hoverTool >= 0 && _hoverTool < _tools.Count)
        {
            var tool = _tools[_hoverTool];
            if (tool.Toggle)
            {
                var state = tool.Active ? "on, " : string.Empty;
                return Interactive
                    ? $"{tool.Label} ({state}click to toggle)"
                    : $"{tool.Label} ({state}hold to toggle)";
            }

            return tool.Hint is { Length: > 0 } hint ? $"{tool.Label} ({hint})" : tool.Label;
        }

        if (_hoverHub) return Interactive ? "Cancel" : "Release here to cancel";

        return Interactive ? _caption : $"{_caption} (release on a format)";
    }

    /// <summary>
    /// Where wedge <paramref name="index"/> starts. The first wedge is centred on 12 o'clock —
    /// matching the macOS app, where the primary format sits at the top — so it begins half a
    /// sweep anticlockwise of the vertical.
    /// </summary>
    private static float WedgeStart(int index, float sweep) => -90f - sweep / 2f + index * sweep;

    /// <summary>Middle-ellipsises a label so long file names still fit the caption line.</summary>
    private static string Shorten(string text, int limit)
    {
        if (text.Length <= limit) return text;

        var keep = (limit - 1) / 2;
        return string.Concat(text.AsSpan(0, keep), "…", text.AsSpan(text.Length - keep));
    }

    private static string KindLabel(MediaKind kind) => kind switch
    {
        MediaKind.Video => "VID",
        MediaKind.Audio => "AUD",
        MediaKind.Image => "IMG",
        MediaKind.Pdf => "PDF",
        MediaKind.Document => "DOC",
        MediaKind.Subtitle => "SUB",
        MediaKind.Delimited => "TBL",
        _ => "FILE",
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
