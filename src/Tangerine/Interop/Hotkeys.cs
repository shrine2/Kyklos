namespace Tangerine.Interop;

/// <summary>Parses "Ctrl+Alt+C" style hotkey strings into Win32 modifier/keys values.</summary>
internal static class Hotkeys
{
    public static readonly string[] Presets =
    [
        "Ctrl+Alt+C", "Ctrl+Alt+T", "Ctrl+Shift+C", "Alt+Shift+C", "Ctrl+Alt+Space", "Win+Shift+C",
    ];

    public static bool TryParse(string spec, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;

            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "win": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    if (!isLast) return false;
                    if (!Enum.TryParse<Keys>(part, ignoreCase: true, out var parsed)) return false;
                    key = (uint)parsed;
                    break;
            }
        }

        return key != 0;
    }

    public static string Describe(string spec) => string.IsNullOrWhiteSpace(spec) ? "none" : spec.Replace("+", " + ");
}
