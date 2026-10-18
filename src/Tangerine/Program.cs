using Tangerine.Core;
using Tangerine.Interop;

namespace Tangerine;

internal static class Program
{
    private const string MutexName = @"Local\TangerineWin.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();

        var files = ExtractFiles(args);
        var settings = Settings.Load();

        // Probe the toolchain before anything can be shown so availability is accurate.
        Toolchain.Resolve(settings);

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var directory = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                            ?? Path.Combine(Paths.TempRoot, "selftest");
            var section = args.SkipWhile(a => !a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && !string.Equals(a, directory, StringComparison.OrdinalIgnoreCase))
                ?? "all";
            try
            {
                var failures = Task.Run(() => SelfTest.RunAsync(directory, section)).GetAwaiter().GetResult();
                NativeMethods.HardExit(failures);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "tangerine-selftest-error.txt"), ex.ToString());
                NativeMethods.HardExit(99);
            }
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Another copy owns the tray. Hand the selection over and exit quietly —
            // this is the path Explorer's "Send to" shortcut takes.
            IpcWindow.Send(files);
            NativeMethods.HardExit(0);
            return;
        }

        if (args.Any(a => a.Equals("--smoketest", StringComparison.OrdinalIgnoreCase)))
        {
            // Boots the real tray UI, then shuts it down the same way the Exit menu item
            // does. Used to prove the GUI starts and tears down without faulting.
            using var smoke = new TrayApp(settings, []);
            using var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (_, _) => { timer.Stop(); smoke.ShutdownForTest(); };
            timer.Start();
            Application.Run(smoke);
            NativeMethods.HardExit(0);
            return;
        }

        using var app = new TrayApp(settings, files);
        Application.Run(app);
        NativeMethods.HardExit(0);
    }

    /// <summary>Everything that is not a switch and exists on disk is treated as an input file.</summary>
    private static string[] ExtractFiles(string[] args)
        => args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Writes an unhandled exception to disk so a silent crash is diagnosable.</summary>
    private static void ReportFatal(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Settings.DirPath);
            File.AppendAllText(
                Path.Combine(Settings.DirPath, "crash.log"),
                $"[{DateTime.Now:u}] {exception}{Environment.NewLine}{Environment.NewLine}");

            MessageBox.Show(
                $"Tangerine hit an unexpected error and will close.\n\n{exception?.Message}\n\nDetails were written to:\n{Path.Combine(Settings.DirPath, "crash.log")}",
                "Tangerine", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}
