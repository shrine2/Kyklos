using Microsoft.Win32;

namespace Kyklos.Core;

/// <summary>Explorer "Send to" integration and the run-at-login registration.</summary>
public static class ShellIntegration
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Kyklos";

    public static string SendToFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "SendTo");

    public static string SendToShortcut => Path.Combine(SendToFolder, "Convert with Kyklos.lnk");

    public static bool SendToInstalled => File.Exists(SendToShortcut);

    public static void InstallSendTo()
    {
        var exe = ExecutablePath;
        Directory.CreateDirectory(SendToFolder);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell is unavailable on this system.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(SendToShortcut);
        shortcut.TargetPath = exe;
        shortcut.Arguments = "--sendto";
        shortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        shortcut.IconLocation = exe + ",0";
        shortcut.Description = "Convert files with Kyklos";
        shortcut.Save();
    }

    public static void UninstallSendTo()
    {
        try
        {
            if (File.Exists(SendToShortcut)) File.Delete(SendToShortcut);
        }
        catch
        {
            // Explorer may hold the shortcut open; harmless.
        }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;

            if (enabled) key.SetValue(RunValueName, $"\"{ExecutablePath}\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry policy may forbid this; the app still works when launched manually.
        }
    }

    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;

            var main = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(main))
                return Path.ChangeExtension(main, ".exe");

            return Path.Combine(AppContext.BaseDirectory, "Kyklos.exe");
        }
    }
}
