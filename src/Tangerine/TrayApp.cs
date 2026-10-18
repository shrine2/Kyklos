using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tangerine.Core;
using Tangerine.Interop;
using Tangerine.Ui;

namespace Tangerine;

/// <summary>
/// The tray application: owns the tray icon, the global hotkey, the drag trigger,
/// the drop surface, the floating drop target and the radial wheel, and drives
/// conversions when the user picks a format.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly HotkeyWindow _hotkey = new();
    private readonly ModifierWatcher _watcher = new();
    private readonly DropSurfaceForm _surface = new();
    private readonly RadialMenuForm _wheel = new();
    private readonly IpcWindow _ipc = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _dropTargetItem;
    private readonly string[] _startupFiles;

    private DropTargetForm? _dropTarget;
    private ConvertOptions _options;
    private List<string> _pending = [];
    private MediaKind _pendingKind = MediaKind.Unknown;
    private Point _wheelAnchor;
    private bool _startupHandled;

    public TrayApp(Settings settings, string[] startupFiles)
    {
        _settings = settings;
        _startupFiles = startupFiles;
        _options = NewOptions();
        _icon = CreateIcon();

        _statusItem = new ToolStripMenuItem("Checking tools…") { Enabled = false };
        _dropTargetItem = new ToolStripMenuItem("Show floating drop target", null, (_, _) => ToggleDropTarget())
        {
            Checked = _settings.ShowDropTarget,
        };

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "Tangerine — file converter",
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) => OpenSettings();

        WireHotkey();
        WireWatcher();
        WireSurface();
        WireWheel();
        WireDropTarget();
        WireIpc();

        UpdateStatusItem();
        ApplyInteractionSettings();

        if (_startupFiles.Length > 0) Application.Idle += OnFirstIdle;
    }

    private ConvertOptions NewOptions() => new()
    {
        Quality = _settings.Quality,
        StripMetadata = _settings.StripMetadata,
    };

    // ---------------------------------------------------------------- wiring

    private void WireHotkey()
    {
        _hotkey.Pressed += () =>
        {
            // Stop the drag trigger from also firing while Ctrl+Alt+C is held down.
            _watcher.Suppress();
            OpenWheelFromSelection(interactive: true);
        };
    }

    private void WireWatcher()
    {
        _watcher.Armed += () =>
        {
            if (!_settings.EnableDragSurface) return;
            _surface.Arm();
        };

        _watcher.Disarmed += () =>
        {
            _surface.Disarm();
            if (!_wheel.Interactive) CloseWheel();
        };
    }

    private void WireSurface()
    {
        _surface.FilesEntered += (files, point) => OpenWheel(files, point, interactive: false);
        _surface.PointerMoved += point =>
        {
            if (_wheel.Visible) _wheel.DriveHover(point);
        };
        _surface.FilesDropped += (_, _) =>
        {
            if (_wheel.Visible) _wheel.DriveCommit();
            CloseWheel();
            _surface.Disarm();
        };
        _surface.DragLeft += () => CloseWheel();
    }

    private void WireWheel()
    {
        _wheel.TargetChosen += target => StartConversion(target);
        _wheel.ToolToggled += OnToolToggled;
        _wheel.ToolRequested += OnToolRequested;
        _wheel.Cancelled += () => { };
    }

    private void WireDropTarget()
    {
        if (!_settings.ShowDropTarget) return;

        var location = _settings.DropTargetX >= 0 && _settings.DropTargetY >= 0
            ? new Point(_settings.DropTargetX, _settings.DropTargetY)
            : DefaultDropTargetLocation();

        _dropTarget = new DropTargetForm();
        _dropTarget.FilesDropped += (files, point) => OpenWheel(files, point, interactive: true);
        _dropTarget.Clicked += () => OpenWheelFromSelection(interactive: true);
        _dropTarget.PositionChanged += point =>
        {
            _settings.DropTargetX = point.X;
            _settings.DropTargetY = point.Y;
            _settings.Save();
        };
        _dropTarget.ContextRequested += ShowDropTargetMenu;
        _dropTarget.ShowAt(location);
    }

    private void WireIpc()
    {
        _ipc.PayloadReceived += payload =>
        {
            if (payload.Length == 1 && IpcWindow.IsShowCommand(payload[0]))
            {
                OpenWheelFromSelection(interactive: true);
                return;
            }

            var files = payload.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length > 0) OpenWheel(files, NativeMethods.CursorPosition(), interactive: true);
        };
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        if (_startupHandled) return;
        _startupHandled = true;

        var files = _startupFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) return;
        OpenWheel(files, NativeMethods.CursorPosition(), interactive: true);
    }

    // ---------------------------------------------------------------- wheel flow

    private void OpenWheelFromSelection(bool interactive)
    {
        var files = ExplorerSelection.GetSelection();
        if (files.Length == 0) files = ClipboardFiles.GetFiles();

        if (files.Length == 0)
        {
            Notify("Tangerine", "Select files in Explorer (or copy them) and try again.");
            return;
        }

        OpenWheel(files, NativeMethods.CursorPosition(), interactive);
    }

    private void OpenWheel(IReadOnlyList<string> files, Point anchor, bool interactive)
    {
        var usable = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (usable.Length == 0)
        {
            Notify("Tangerine", "No usable files in that selection.");
            return;
        }

        var kind = FormatCatalog.DetectKind(usable);
        if (kind == MediaKind.Unknown)
        {
            Notify("Tangerine", $"No converter for '{Path.GetExtension(usable[0])}' files yet.");
            return;
        }

        _pending = [.. usable];
        _pendingKind = kind;
        _wheelAnchor = anchor;
        _options = NewOptions();

        _wheel.Configure(kind, _pending, _options);
        _wheel.ShowWheel(anchor, interactive);
    }

    private void CloseWheel()
    {
        if (_wheel.Visible) _wheel.HideWheel();
    }

    private void OnToolToggled(ToolId tool)
    {
        switch (tool)
        {
            case ToolId.Compress: _options.Compress = !_options.Compress; break;
            case ToolId.StripMetadata: _options.StripMetadata = !_options.StripMetadata; break;
        }

        _wheel.RefreshOptions(_options);
    }

    private void OnToolRequested(ToolId tool)
    {
        switch (tool)
        {
            case ToolId.TrimResize:
            {
                var updated = AdvancedDialog.Edit(_wheel, _options, _pendingKind);
                if (updated is not null)
                {
                    _options = updated;
                    // Re-open the wheel so the user can finish choosing a format.
                    _wheel.Configure(_pendingKind, _pending, _options);
                    _wheel.ShowWheel(_wheelAnchor, interactive: true);
                }
                break;
            }

            case ToolId.ExtractAudio:
                StartConversion(FormatCatalog.FindTarget(_pendingKind, "mp3"));
                break;

            case ToolId.MakeGif:
                StartConversion(FormatCatalog.FindTarget(_pendingKind, "gif"));
                break;

            case ToolId.ToPdf:
                StartConversion(FormatCatalog.FindTarget(_pendingKind, "pdf"));
                break;
        }
    }

    private async void StartConversion(MediaTarget? target)
    {
        if (target is null)
        {
            Notify("Tangerine", "That conversion is not available for these files.");
            return;
        }

        if (!target.Available)
        {
            Notify("Tangerine", target.UnavailableReason ?? $"{target.Label} is unavailable on this machine.");
            return;
        }

        var files = _pending.ToList();
        if (files.Count == 0) return;

        var options = _options.Clone();

        var progress = new ProgressForm($"{files.Count} file(s) → {target.Label}", files.Count);
        progress.Show();
        progress.FormClosed += (_, _) => progress.Dispose();

        try
        {
            var reporter = new Progress<BatchProgress>(progress.Report);
            var results = await BatchConverter.RunAsync(files, target, options, _settings, reporter, progress.Token);
            progress.Finish(results);

            var failed = results.Count(r => !r.Success);
            if (failed > 0) Notify("Tangerine", $"{failed} of {results.Count} conversions failed — see the log window.");
        }
        catch (OperationCanceledException)
        {
            progress.MarkCancelled();
            progress.Finish([]);
        }
        catch (Exception ex)
        {
            progress.Finish([new ConvertResult(false, false, files[0], null, ex.Message, 0)]);
        }
    }

    // ---------------------------------------------------------------- tray

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Dark.Panel,
            ForeColor = Dark.Text,
            Renderer = new FlatDarkRenderer(),
            ShowImageMargin = false,
        };

        menu.Items.Add(new ToolStripMenuItem($"Convert selection  ({Hotkeys.Describe(_settings.Hotkey)})", null,
            (_, _) => OpenWheelFromSelection(interactive: true)));
        menu.Items.Add(new ToolStripMenuItem("Convert copied files", null, (_, _) =>
        {
            var files = ClipboardFiles.GetFiles();
            if (files.Length == 0)
            {
                Notify("Tangerine", "The clipboard has no copied files. Select files and press Ctrl+C first.");
                return;
            }
            OpenWheel(files, NativeMethods.CursorPosition(), interactive: true);
        }));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_dropTargetItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripMenuItem("Re-scan tools", null, (_, _) => RescanTools()));
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        return menu;
    }

    private void ShowDropTargetMenu(Point screenPoint)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Dark.Panel,
            ForeColor = Dark.Text,
            Renderer = new FlatDarkRenderer(),
            ShowImageMargin = false,
        };

        menu.Items.Add(new ToolStripMenuItem("Convert selection", null, (_, _) => OpenWheelFromSelection(interactive: true)));
        menu.Items.Add(new ToolStripMenuItem("Hide this icon", null, (_, _) =>
        {
            _settings.ShowDropTarget = false;
            _settings.Save();
            ToggleDropTarget();
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        menu.Show(screenPoint);
    }

    private void OpenSettings()
    {
        var updated = SettingsForm.Edit(_wheel, _settings);
        if (updated is null) return;

        ApplyInteractionSettings();
        UpdateStatusItem();
        _tray.ContextMenuStrip = BuildTrayMenu();
    }

    private void ApplyInteractionSettings()
    {
        _hotkey.Register(_settings.Hotkey);

        _watcher.Spec = _settings.DragModifiers;
        _watcher.RequirePointerDrag = true;
        _watcher.Enabled = _settings.EnableDragSurface;

        if (!_settings.EnableDragSurface)
        {
            _watcher.Enabled = false;
            _surface.Disarm();
        }

        if (_settings.ShowDropTarget && _dropTarget is null)
        {
            WireDropTarget();
        }
        else if (!_settings.ShowDropTarget && _dropTarget is not null)
        {
            _dropTarget.Dispose();
            _dropTarget = null;
        }
    }

    private void ToggleDropTarget()
    {
        _settings.ShowDropTarget = !_settings.ShowDropTarget;
        _settings.Save();
        ApplyInteractionSettings();
        _dropTargetItem.Checked = _settings.ShowDropTarget;
    }

    private void RescanTools()
    {
        Toolchain.Resolve(_settings);
        UpdateStatusItem();
    }

    private void UpdateStatusItem()
    {
        var ffmpeg = Toolchain.FfmpegReady ? $"ffmpeg ok ({Toolchain.Caps.Encoders.Count} encoders)" : "ffmpeg missing";
        var office = Toolchain.OfficeReady ? "LibreOffice ok" : "LibreOffice missing";
        _statusItem.Text = $"{ffmpeg} · {office}";

        _tray.Text = Toolchain.FfmpegReady
            ? "Tangerine — ready"
            : "Tangerine — ffmpeg not found (open Settings)";
    }

    private void ShowAbout()
    {
        var message =
            $"Tangerine for Windows{Environment.NewLine}{Environment.NewLine}" +
            $"ffmpeg: {(Toolchain.FfmpegReady ? Toolchain.Ffmpeg : "not found")}{Environment.NewLine}" +
            $"{Toolchain.FfmpegVersion}{Environment.NewLine}" +
            $"LibreOffice: {(Toolchain.OfficeReady ? Toolchain.Soffice : "not found (document conversions disabled)")}{Environment.NewLine}{Environment.NewLine}" +
            $"Drag trigger: hold {_settings.DragModifiers} and drag a file in Explorer.{Environment.NewLine}" +
            $"Hotkey: {Hotkeys.Describe(_settings.Hotkey)}{Environment.NewLine}" +
            $"Output: {_settings.OutputMode}, {_settings.OverwriteMode}{Environment.NewLine}" +
            $"Settings file: {Settings.FilePath}";

        MessageBox.Show(message, "About Tangerine", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Notify(string title, string message)
    {
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = message.Length > 250 ? message[..250] : message;
            _tray.ShowBalloonTip(4000);
        }
        catch
        {
            // Balloon tips can be suppressed by policy; ignore.
        }
    }

    private static Point DefaultDropTargetLocation()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(area.Right - 110, area.Bottom - 170);
    }

    private void ExitApplication()
    {
        _tray.Visible = false;
        ExitThread();
    }

    /// <summary>Test hook: shuts the tray down exactly as the Exit menu item does.</summary>
    internal void ShutdownForTest() => ExitApplication();

    // ---------------------------------------------------------------- icon

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Theme.AccentSoft, Theme.Accent, 60f))
                g.FillEllipse(fill, 3, 5, 26, 26);

            using (var leaf = new SolidBrush(Color.FromArgb(255, 108, 186, 96)))
                g.FillEllipse(leaf, 15, 1, 12, 8);

            using (var rim = new Pen(Color.FromArgb(110, 255, 255, 255), 1.4f))
                g.DrawEllipse(rim, 3, 5, 26, 26);
        }

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

    // ---------------------------------------------------------------- teardown

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _hotkey.Dispose();
            _ipc.Dispose();
            _surface.Dispose();
            _wheel.Dispose();
            _dropTarget?.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Flat dark renderer so the tray menu matches the rest of the UI.</summary>
    private sealed class FlatDarkRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Dark.Panel);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Dark.FieldBack : Dark.Panel);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Dark.Text : Dark.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Dark.Border);
            e.Graphics.DrawLine(pen, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
        }
    }
}
