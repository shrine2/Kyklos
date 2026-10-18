using System.Diagnostics;
using Microsoft.Win32;

namespace Kyklos.Core;

/// <summary>
/// Locates and probes the external tools Kyklos drives (ffmpeg, ffprobe, LibreOffice).
/// Everything is optional: the wheel only shows targets the current machine can produce.
/// </summary>
public static class Toolchain
{
    private static readonly Lock Gate = new();

    public static string? Ffmpeg { get; private set; }
    public static string? Ffprobe { get; private set; }
    public static string? Soffice { get; private set; }
    public static FfmpegCaps Caps { get; private set; } = new();
    public static string? FfmpegError { get; private set; }
    public static string FfmpegVersion { get; private set; } = "unknown";

    public static bool FfmpegReady => Ffmpeg is not null;
    public static bool OfficeReady => Soffice is not null;

    /// <summary>Raised after a Resolve() completes so open UI can refresh its availability flags.</summary>
    public static event Action? Changed;

    public static void Resolve(Settings settings)
    {
        string? ffmpeg = null;
        var caps = new FfmpegCaps();
        string? error = null;
        var version = "unknown";

        try
        {
            ffmpeg = FindFfmpeg(settings.FfmpegPath);
            if (ffmpeg is null)
            {
                error = "ffmpeg.exe was not found. Install it with: winget install Gyan.FFmpeg";
            }
            else
            {
                var encoders = RunCapture(ffmpeg, "-hide_banner -encoders");
                var decoders = RunCapture(ffmpeg, "-hide_banner -decoders");
                var muxers = RunCapture(ffmpeg, "-hide_banner -muxers");
                caps = FfmpegCaps.Parse(encoders, decoders, muxers);
                version = ParseVersion(RunCapture(ffmpeg, "-hide_banner -version"));
                if (caps.Encoders.Count == 0)
                {
                    error = "ffmpeg responded but no encoders were reported.";
                    ffmpeg = null;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            ffmpeg = null;
        }

        lock (Gate)
        {
            Ffmpeg = ffmpeg;
            Ffprobe = FindFfprobe(ffmpeg);
            Soffice = FindSoffice(settings.LibreOfficePath);
            Caps = caps;
            FfmpegError = error;
            FfmpegVersion = version;
        }

        Changed?.Invoke();
    }

    public static Task ResolveAsync(Settings settings) => Task.Run(() => Resolve(settings));

    private static string ParseVersion(string versionOutput)
    {
        var line = versionOutput.Split('\n').FirstOrDefault(l => l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase));
        return line?.Trim() ?? "unknown";
    }

    // ---------------------------------------------------------------- discovery

    private static string? FindFfmpeg(string? configured)
    {
        foreach (var candidate in CandidateFiles(configured, "ffmpeg.exe"))
        {
            if (File.Exists(candidate)) return candidate;
        }

        var onPath = FindOnPath("ffmpeg.exe");
        if (onPath is not null) return onPath;

        // winget installs land in Packages\<id>\<version>\bin
        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        foreach (var pattern in new[] { "Gyan.FFmpeg*", "Gyan.FFmpeg*", "*FFmpeg*" })
        {
            var found = SearchDirectories(packages, pattern, "ffmpeg.exe");
            if (found is not null) return found;
        }

        var links = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        if (File.Exists(links)) return links;

        return null;
    }

    private static string? FindFfprobe(string? ffmpegPath)
    {
        if (ffmpegPath is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
            if (File.Exists(sibling)) return sibling;
        }
        return FindOnPath("ffprobe.exe");
    }

    private static string? FindSoffice(string? configured)
    {
        var candidates = CandidateFiles(configured, "soffice.exe").ToList();
        candidates.AddRange(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
        ]);

        // LibreOffice records its install root here on every install type.
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uno = baseKey.OpenSubKey(@"SOFTWARE\LibreOffice\UNO");
                    var installPath = uno?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(installPath))
                        candidates.Add(Path.Combine(installPath, "soffice.exe"));
                }
                catch
                {
                    // registry view unavailable
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        }

        return FindOnPath("soffice.exe");
    }

    private static IEnumerable<string> CandidateFiles(string? configured, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Accept either the exe itself or the folder containing it.
            if (Directory.Exists(configured))
                yield return Path.Combine(configured, exeName);
            else
                yield return configured;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        yield return Path.Combine(AppContext.BaseDirectory, exeName);
    }

    private static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // malformed PATH entry
            }
        }
        return null;
    }

    private static string? SearchDirectories(string root, string directoryPattern, string exeName)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var dir in Directory.EnumerateDirectories(root, directoryPattern))
            {
                foreach (var file in Directory.EnumerateFiles(dir, exeName, SearchOption.AllDirectories))
                    return file;
            }
        }
        catch
        {
            // permission noise inside the winget cache
        }
        return null;
    }

    // ---------------------------------------------------------------- process helper

    private static string RunCapture(string exe, string arguments, int timeoutMs = 30_000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        return stdout + "\n" + stderr;
    }
}
