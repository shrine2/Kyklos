namespace Tangerine.Interop;

/// <summary>
/// Reads the file list from the clipboard (CF_HDROP), which is what Explorer fills in
/// when the user presses Ctrl+C on a selection. Used by the global hotkey path.
/// </summary>
internal static class ClipboardFiles
{
    public static string[] GetFiles()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsFileDropList()) return [];
                var collection = Clipboard.GetFileDropList();
                return collection.Cast<string>()
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                // Clipboard is locked by another process — retry briefly.
                Thread.Sleep(50);
            }
        }
        return [];
    }
}

/// <summary>
/// Reads the current Explorer window's selection through the Shell.Application COM object,
/// so the hotkey works without the user having to copy anything first.
/// </summary>
internal static class ExplorerSelection
{
    public static string[] GetSelection()
    {
        var results = new List<string>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return [];

            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    var executable = (string)window.FullName;
                    if (!executable.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                    dynamic document = window.Document;
                    dynamic selectedItems = document.SelectedItems();
                    var count = (int)selectedItems.Count;

                    if (count == 0)
                    {
                        dynamic focused = document.FocusedItem;
                        if (focused is not null) AddPath(results, (string?)focused.Path);
                        continue;
                    }

                    foreach (dynamic item in selectedItems)
                    {
                        try { AddPath(results, (string?)item.Path); }
                        catch { /* item vanished mid-enumeration */ }
                    }
                }
                catch
                {
                    // Window closed while enumerating, or a non-shell window.
                }
            }
        }
        catch
        {
            // Shell.Application unavailable (locked-down system).
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPath(List<string> results, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (results.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        results.Add(path);
    }
}
