using System.Text.Json;

namespace Tangerine.Core;

public static class OutputModes
{
    public const string Beside = "beside";
    public const string Subfolder = "subfolder";
}

public static class OverwriteModes
{
    public const string Rename = "rename";
    public const string Overwrite = "overwrite";
    public const string Skip = "skip";
}

/// <summary>User configuration, persisted to %APPDATA%\TangerineWin\settings.json.</summary>
public sealed class Settings
{
    /// <summary>Explicit path to ffmpeg.exe. Null = auto-detect.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Explicit path to soffice.exe. Null = auto-detect.</summary>
    public string? LibreOfficePath { get; set; }

    public string OutputMode { get; set; } = OutputModes.Beside;
    public string SubfolderName { get; set; } = "converted";
    public string OverwriteMode { get; set; } = OverwriteModes.Rename;

    /// <summary>Default for the "No meta" wheel toggle.</summary>
    public bool StripMetadata { get; set; }

    public string Quality { get; set; } = "balanced";

    /// <summary>Arm the full-screen drop surface while the drag modifiers are held.</summary>
    public bool EnableDragSurface { get; set; } = true;

    /// <summary>Modifier combination that arms the drop surface. "Ctrl+Alt" | "Shift" | "Ctrl+Shift" | "Alt".</summary>
    public string DragModifiers { get; set; } = "Ctrl+Alt";

    /// <summary>Show the small floating drop target on the desktop.</summary>
    public bool ShowDropTarget { get; set; } = true;

    public int DropTargetX { get; set; } = -1;
    public int DropTargetY { get; set; } = -1;

    public bool StartWithWindows { get; set; }

    public string Hotkey { get; set; } = "Ctrl+Alt+C";

    public bool FirstRunCompleted { get; set; }

    public static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TangerineWin");

    public static string FilePath => Path.Combine(DirPath, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable config: fall back to defaults rather than failing to start.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Non-fatal: the app keeps running with in-memory settings.
        }
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}
