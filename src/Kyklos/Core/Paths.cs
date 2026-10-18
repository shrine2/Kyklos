namespace Kyklos.Core;

/// <summary>Output path resolution and small formatting helpers.</summary>
public static class Paths
{
    /// <summary>
    /// Resolves where a converted file should go, mirroring Tangerine's "save beside the original" behaviour.
    /// Returns ShouldSkip=true when the overwrite policy is Skip and the target already exists.
    /// </summary>
    public static (string Path, bool ShouldSkip) ResolveOutput(string sourcePath, string targetExt, Settings settings)
    {
        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath))
                        ?? Environment.CurrentDirectory;

        var dir = sourceDir;
        if (settings.OutputMode == OutputModes.Subfolder)
        {
            dir = Path.Combine(sourceDir, string.IsNullOrWhiteSpace(settings.SubfolderName) ? "converted" : settings.SubfolderName);
        }

        var ext = targetExt.TrimStart('.');
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrEmpty(baseName)) baseName = "output";

        var candidate = Path.Combine(dir, $"{baseName}.{ext}");

        if (settings.OverwriteMode == OverwriteModes.Overwrite)
            return (candidate, false);

        if (settings.OverwriteMode == OverwriteModes.Skip)
            return (candidate, File.Exists(candidate));

        if (!File.Exists(candidate))
            return (candidate, false);

        for (var i = 1; i < 10_000; i++)
        {
            var next = Path.Combine(dir, $"{baseName} ({i}).{ext}");
            if (!File.Exists(next)) return (next, false);
        }

        return (candidate, false);
    }

    /// <summary>Prepends the multi-page suffix used by PDF page rendering / archives.</summary>
    public static string WithSuffix(string path, string suffix)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{name} {suffix}{ext}");
    }

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Ensures the directory exists and returns it.</summary>
    public static string EnsureDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "Kyklos");

    public static string NewTempDir()
    {
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteTree(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    public static void TryDeleteFile(string? file)
    {
        if (string.IsNullOrEmpty(file)) return;
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // best effort
        }
    }
}
