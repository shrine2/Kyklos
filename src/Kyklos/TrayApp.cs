using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Kyklos.Core;
using Kyklos.Interop;
using Kyklos.Ui;

namespace Kyklos;

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
    private CancellationTokenSource? _cancel;

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
            Text = "Kyklos — file converter",
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
            if (_wheel.Busy) return; // one conversion at a time
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

            // A successful commit leaves the wheel running the job; otherwise it just goes away.
            if (!_wheel.Busy) CloseWheel();
            _surface.Disarm();
        };
        _surface.DragLeft += () => CloseWheel();
    }

    private void WireWheel()
    {
        _wheel.TargetChosen += target => _ = RunConversionAsync(target);
        _wheel.ToolToggled += OnToolToggled;
        _wheel.ToolRequested += OnToolRequested;
        _wheel.CancelRequested += CancelConversion;
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
            Notify("Kyklos", "Select files in Explorer (or copy them) and try again.");
            return;
        }

        OpenWheel(files, NativeMethods.CursorPosition(), interactive);
    }

    private void OpenWheel(IReadOnlyList<string> files, Point anchor, bool interactive)
    {
        if (_wheel.Busy) return; // a conversion is already running

        var usable = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (usable.Length == 0)
        {
            Notify("Kyklos", "No usable files in that selection.");
            return;
        }

        var kind = FormatCatalog.DetectKind(usable);
        if (kind == MediaKind.Unknown)
        {
            Notify("Kyklos", $"No converter for '{Path.GetExtension(usable[0])}' files yet.");
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
        // While a job is running the wheel is the progress display, so nothing may close it
        // behind the user's back; the batch itself ends it.
        if (_wheel.Busy) return;
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
                CloseWheel();
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
                _ = RunConversionAsync(FormatCatalog.FindTarget(_pendingKind, "mp3"));
                break;

            case ToolId.MakeGif:
                _ = RunConversionAsync(FormatCatalog.FindTarget(_pendingKind, "gif"));
                break;

            case ToolId.ToPdf:
                _ = RunConversionAsync(FormatCatalog.FindTarget(_pendingKind, "pdf"));
                break;
        }
    }

    /// <summary>Asks the running batch to stop; the hub of the wheel is the cancel button.</summary>
    private void CancelConversion() => _cancel?.Cancel();

    private async Task<IReadOnlyList<ConvertResult>> RunConversionAsync(MediaTarget? target)
    {
        if (target is null)
        {
            CloseWheel();
            Notify("Kyklos", "That conversion is not available for these files.");
            return [];
        }

        if (!target.Available)
        {
            CloseWheel();
            Notify("Kyklos", target.UnavailableReason ?? $"{target.Label} is unavailable on this machine.");
            return [];
        }

        var files = _pending.ToList();
        if (files.Count == 0)
        {
            CloseWheel();
            return [];
        }

        var options = _options.Clone();

        // The wheel is the whole progress UI: the hub spins while the batch runs, then the
        // wheel fades out. Nothing else opens, so back-to-back conversions never stack windows.
        _cancel = new CancellationTokenSource();
        _wheel.BeginBusy(files.Count == 1
            ? $"Converting to {target.Label}"
            : $"Converting {files.Count} files to {target.Label}");

        try
        {
            var reporter = new Progress<BatchProgress>(report =>
            {
                if (report.Result is not null) return; // the "finished" half of each pair
                var caption = report.Total > 1
                    ? $"Converting {report.Done + 1} / {report.Total}"
                    : "Converting";
                _wheel.UpdateBusy(caption, Path.GetFileName(report.Source));
            });

            var results = await BatchConverter.RunAsync(files, target, options, _settings, reporter, _cancel.Token);
            var failed = results.Where(r => !r.Success).ToList();

            _wheel.EndBusy();

            if (failed.Count > 0)
            {
                var log = LogFailures(target, failed);
                Notify("Kyklos", $"{failed.Count} of {results.Count} conversions failed — details in {log}.");
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            _wheel.EndBusy();
            return [];
        }
        catch (Exception ex)
        {
            _wheel.EndBusy();
            var log = LogFailures(target, [new ConvertResult(false, false, files[0], null, ex.Message, 0)]);
            Notify("Kyklos", $"Conversion failed — details in {log}.");
            return [];
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    /// <summary>
    /// Writes failed conversions to a log file. There is no log window any more, so this is
    /// where the detail lives when a balloon tip is not enough.
    /// </summary>
    private static string LogFailures(MediaTarget target, IEnumerable<ConvertResult> failures)
    {
        var path = Path.Combine(Settings.DirPath, "conversions.log");

        try
        {
            Directory.CreateDirectory(Settings.DirPath);
            using var writer = new StreamWriter(path, append: true);
            writer.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  -> {target.Label} ---");
            foreach (var failure in failures)
                writer.WriteLine($"{failure.Source}\n    {failure.Error}");
        }
        catch
        {
            // Logging must never take the app down.
        }

        return path;
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
                Notify("Kyklos", "The clipboard has no copied files. Select files and press Ctrl+C first.");
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
        var menu = BuildDropTargetMenu();

        // The menu is owned by nothing, so it has to be disposed by hand — but never from inside
        // its own Closed event. WinForms is still unwinding the click at that point and goes on
        // to touch the strip, which fails with "Cannot access a disposed object".
        menu.Closed += (_, _) => Post(menu.Dispose);
        menu.Show(screenPoint);
    }

    private ContextMenuStrip BuildDropTargetMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Dark.Panel,
            ForeColor = Dark.Text,
            Renderer = new FlatDarkRenderer(),
            ShowImageMargin = false,
        };

        // Every one of these defers its work to the next message-loop turn: the handler for the
        // item we are in the middle of may dispose this very menu, or the window it came from.
        menu.Items.Add(new ToolStripMenuItem("Convert selection", null, (_, _) => Post(() => OpenWheelFromSelection(interactive: true))));
        menu.Items.Add(new ToolStripMenuItem("Hide this icon", null, (_, _) => Post(() => SetDropTargetVisible(false))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => Post(OpenSettings)));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Post(ExitApplication)));

        return menu;
    }

    /// <summary>
    /// Queues <paramref name="action"/> for the next turn of the message loop, so it runs after
    /// the current event — typically a menu click — has finished being processed.
    /// </summary>
    private static void Post(Action action)
    {
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            action();
            return;
        }

        context.Post(_ => action(), null);
    }

    private void OpenSettings()
    {
        var updated = SettingsForm.Edit(_wheel, _settings);
        if (updated is null) return;

        ApplyInteractionSettings();
        UpdateStatusItem();

        // Swap the tray menu, disposing the old strip on a later turn: the click that got us
        // here may still be unwinding through it.
        var previous = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildTrayMenu();
        if (previous is not null) Post(previous.Dispose);
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

    /// <summary>Shows or hides the floating drop target, persisting the choice.</summary>
    private void SetDropTargetVisible(bool visible)
    {
        _settings.ShowDropTarget = visible;
        _settings.Save();
        ApplyInteractionSettings();
        _dropTargetItem.Checked = visible;
    }

    private void ToggleDropTarget() => SetDropTargetVisible(!_settings.ShowDropTarget);

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
            ? "Kyklos — ready"
            : "Kyklos — ffmpeg not found (open Settings)";
    }

    private void ShowAbout()
    {
        var message =
            $"Kyklos for Windows{Environment.NewLine}{Environment.NewLine}" +
            $"ffmpeg: {(Toolchain.FfmpegReady ? Toolchain.Ffmpeg : "not found")}{Environment.NewLine}" +
            $"{Toolchain.FfmpegVersion}{Environment.NewLine}" +
            $"LibreOffice: {(Toolchain.OfficeReady ? Toolchain.Soffice : "not found (document conversions disabled)")}{Environment.NewLine}{Environment.NewLine}" +
            $"Drag trigger: hold {_settings.DragModifiers} and drag a file in Explorer.{Environment.NewLine}" +
            $"Hotkey: {Hotkeys.Describe(_settings.Hotkey)}{Environment.NewLine}" +
            $"Output: {_settings.OutputMode}, {_settings.OverwriteMode}{Environment.NewLine}" +
            $"Settings file: {Settings.FilePath}";

        MessageBox.Show(message, "About Kyklos", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

    /// <summary>
    /// Test hook for <c>--uipreview</c>: opens the wheel for a sample file and pushes it into
    /// the running state, so the spinner hub and caption can be inspected without a real job.
    /// </summary>
    internal void ShowWheelPreviewForTest(string? sample)
    {
        _pending = sample is not null && File.Exists(sample) ? [sample] : [];
        _pendingKind = _pending.Count > 0
            ? FormatCatalog.DetectKind(_pending)
            : MediaKind.Image;
        _wheelAnchor = NativeMethods.CursorPosition();
        _options = NewOptions();

        _wheel.Configure(_pendingKind, _pending, _options);
        _wheel.ShowWheel(_wheelAnchor, interactive: true);

        _wheel.BeginBusy("Converting 4 files to WEBP");
        _wheel.UpdateBusy("Converting 3 / 12", "holiday-lisbon-raw-shot.png");
    }

    /// <summary>Test hook for <c>--uipreview</c>: ends the simulated batch so the fade runs.</summary>
    internal void EndPreviewBusyForTest() => _wheel.EndBusy();

    /// <summary>Test hook: is the floating icon on screen right now?</summary>
    internal bool DropTargetVisibleForTest => _dropTarget is { Visible: true };

    /// <summary>
    /// Test hook: opens the floating icon's own menu and clicks "Hide this icon" — the exact path
    /// that used to fault, because that handler disposes the window the menu came from and the
    /// menu disposes itself as it closes. Returns false if the menu lost its Hide item.
    /// </summary>
    internal bool ExerciseDropTargetMenuForTest()
    {
        SetDropTargetVisible(true);
        if (_dropTarget is null) return false;

        var menu = BuildDropTargetMenu();
        var hide = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Hide this icon");
        if (hide is null)
        {
            menu.Dispose();
            return false;
        }

        menu.Closed += (_, _) => Post(menu.Dispose);
        menu.Show(NativeMethods.CursorPosition());
        hide.PerformClick();
        menu.Close();

        return true;
    }

    /// <summary>
    /// Test hook: runs one real conversion through the same code path the wheel uses, so the
    /// commit → spinner → batch → fade glue is covered end to end and not just by inspection.
    /// The target is chosen automatically as the first available format for the sample.
    /// </summary>
    internal Task<IReadOnlyList<ConvertResult>> RunConversionForTest(string source)
    {
        _pending = [source];
        _pendingKind = FormatCatalog.DetectKind(_pending);
        _wheelAnchor = NativeMethods.CursorPosition();
        _options = NewOptions();

        // The wheel has to exist before BeginBusy can draw a spinner into it.
        _wheel.Configure(_pendingKind, _pending, _options);
        _wheel.ShowWheel(_wheelAnchor, interactive: true);

        var target = FormatCatalog.TargetsFor(_pendingKind, source).FirstOrDefault(t => t.Available);
        return target is null
            ? Task.FromResult<IReadOnlyList<ConvertResult>>([])
            : RunConversionAsync(target);
    }

    /// <summary>
    /// Test hook: walks show → hide → show on the floating drop target, which is the path the
    /// "Hide this icon" menu item takes. It proves the form is created, disposed and re-created
    /// cleanly instead of being left half-torn-down. Returns false if the state does not settle.
    /// </summary>
    internal bool ExerciseInteractionForTest()
    {
        var original = _settings.ShowDropTarget;

        SetDropTargetVisible(true);
        var shown = _dropTarget is { Visible: true };

        SetDropTargetVisible(false);
        var hidden = _dropTarget is null;

        SetDropTargetVisible(true);
        var reshown = _dropTarget is { Visible: true };

        SetDropTargetVisible(original);

        return shown && hidden && reshown;
    }

    // ---------------------------------------------------------------- icon

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // A ring, not a fruit: Kyklos is Greek for circle, and the app is a radial wheel.
            // Alternate fill mode cuts the middle out so the tray background shows through.
            using var ring = new GraphicsPath(FillMode.Alternate);
            ring.AddEllipse(3, 3, 26, 26);
            ring.AddEllipse(11, 11, 10, 10);

            using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Theme.AccentSoft, Theme.Accent, 60f))
                g.FillPath(fill, ring);

            using var rim = new Pen(Color.FromArgb(110, 255, 255, 255), 1.2f);
            g.DrawEllipse(rim, 3, 3, 26, 26);
            g.DrawEllipse(rim, 11, 11, 10, 10);
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
