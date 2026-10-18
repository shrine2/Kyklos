using System.Runtime.InteropServices;

namespace Tangerine.Interop;

/// <summary>
/// Hidden window used to hand a file list from a second process (for example the
/// Explorer "Send to" shortcut) to the already-running tray instance.
/// </summary>
internal sealed class IpcWindow : NativeWindow, IDisposable
{
    private const string WindowTitle = "Tangerine";

    private const string ShowCommand = "--show";

    public event Action<string[]>? PayloadReceived;

    /// <summary>False when the hidden receiver window could not be created; IPC is then simply unavailable.</summary>
    public bool Available { get; private set; }

    public IpcWindow()
    {
        // The class name is deliberately left unset: WinForms only auto-registers its own
        // generated window class, so a hand-picked name fails with Win32 1411
        // (ERROR_CLASS_DOES_NOT_EXIST). The window is located by title instead.
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = WindowTitle,
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
            });
            Available = true;
        }
        catch
        {
            // Without a receiver window the tray still works; only file hand-off is lost.
            Available = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_COPYDATA)
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(m.LParam);
                var text = data.lpData == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(data.lpData, data.cbData / 2) ?? string.Empty;

                var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) PayloadReceived?.Invoke(parts);
            }
            catch
            {
                // Malformed payload: ignore rather than fault the message loop.
            }
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Sends a payload to the running instance. Returns false when no instance is listening.</summary>
    public static bool Send(IReadOnlyList<string> files)
    {
        var target = NativeMethods.FindWindow(null, WindowTitle);
        if (target == IntPtr.Zero) return false;

        var text = files.Count == 0 ? ShowCommand : string.Join('\n', files);
        var buffer = Marshal.StringToHGlobalUni(text);

        try
        {
            var data = new NativeMethods.COPYDATASTRUCT
            {
                dwData = IntPtr.Zero,
                cbData = (text.Length + 1) * 2,
                lpData = buffer,
            };
            NativeMethods.SendMessage(target, NativeMethods.WM_COPYDATA, IntPtr.Zero, ref data);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsShowCommand(string payload) => payload == ShowCommand;

    public void Dispose()
    {
        try { DestroyHandle(); }
        catch { /* already destroyed */ }
    }
}
