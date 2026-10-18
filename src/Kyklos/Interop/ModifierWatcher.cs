using Kyklos.Core;

namespace Kyklos.Interop;

/// <summary>Message-only window that owns the global hotkey registration.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x7A11;
    private static readonly IntPtr HwndMessage = new(-3);

    private bool _registered;

    public event Action? Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "KyklosHotkey",
            Parent = HwndMessage,
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Style = 0,
            ExStyle = 0,
        });
    }

    /// <summary>Replaces the current registration. Returns false when another app already owns the combination.</summary>
    public bool Register(string spec)
    {
        Unregister();

        if (!Hotkeys.TryParse(spec, out var modifiers, out var key)) return false;

        _registered = NativeMethods.RegisterHotKey(Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, key);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        try { NativeMethods.UnregisterHotKey(Handle, HotkeyId); }
        catch { /* window already gone */ }
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        try { DestroyHandle(); }
        catch { /* handle already destroyed */ }
    }
}

/// <summary>
/// Polls the modifier keys and reports when the configured drag-trigger combination is held.
/// Polling (rather than a keyboard hook) keeps the app out of the low-level input stream —
/// it only ever reads whether Ctrl/Alt/Shift/Win are down, never which keys were typed.
/// </summary>
internal sealed class ModifierWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private bool _armed;
    private bool _suppressed;
    private bool _enabled;

    public event Action? Armed;
    public event Action? Disarmed;

    public ModifierWatcher()
    {
        _timer = new System.Windows.Forms.Timer { Interval = 40 };
        _timer.Tick += (_, _) => Evaluate();
    }

    public string Spec { get; set; } = "Ctrl+Alt";

    /// <summary>
    /// When true (default) the trigger also requires the left mouse button to be held down over an
    /// Explorer file view. That means plain clicks and app drags are never intercepted, and the
    /// surface only appears once a real file drag is under way.
    /// </summary>
    public bool RequirePointerDrag { get; set; } = true;

    public bool IsArmed => _armed;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _timer.Enabled = value;
            if (!value)
            {
                _suppressed = false;
                SetArmed(false);
            }
        }
    }

    /// <summary>Ignores the trigger until every modifier is released (used right after the hotkey fires).</summary>
    public void Suppress() => _suppressed = true;

    private void Evaluate()
    {
        var ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        var alt = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
        var shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
        var win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

        if (_suppressed)
        {
            if (!ctrl && !alt && !shift && !win) _suppressed = false;
            SetArmed(false);
            return;
        }

        var spec = Spec;
        var modifiersHeld =
            (spec.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)
             || spec.Contains("Alt", StringComparison.OrdinalIgnoreCase)
             || spec.Contains("Shift", StringComparison.OrdinalIgnoreCase)
             || spec.Contains("Win", StringComparison.OrdinalIgnoreCase)) &&
            (!spec.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) || ctrl) &&
            (!spec.Contains("Alt", StringComparison.OrdinalIgnoreCase) || alt) &&
            (!spec.Contains("Shift", StringComparison.OrdinalIgnoreCase) || shift) &&
            (!spec.Contains("Win", StringComparison.OrdinalIgnoreCase) || win);

        if (!modifiersHeld)
        {
            SetArmed(false);
            return;
        }

        // Already armed: stay armed until the gesture ends, even if the cursor leaves Explorer.
        if (_armed)
        {
            if (RequirePointerDrag && !NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON)) SetArmed(false);
            return;
        }

        if (RequirePointerDrag)
        {
            if (!NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON)) return;
            if (!NativeMethods.IsOverShellSurface(NativeMethods.CursorPosition())) return;
        }

        SetArmed(true);
    }

    private void SetArmed(bool value)
    {
        if (_armed == value) return;
        _armed = value;
        if (value) Armed?.Invoke();
        else Disarmed?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
