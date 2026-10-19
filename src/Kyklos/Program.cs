using Kyklos.Core;
using Kyklos.Interop;

namespace Kyklos;

internal static class Program
{
    private const string MutexName = @"Local\Kyklos.SingleInstance";

    /// <summary>Set by the diagnostic switches so a fault is recorded rather than shown in a dialog.</summary>
    private static bool _quiet;

    private static int _uiFaults;

    /// <summary>UI-thread exceptions seen so far; the smoke test treats any as a failure.</summary>
    internal static int UiFaultCount => _uiFaults;

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // WinForms swallows exceptions thrown inside event handlers and, with nothing hooked
        // here, shows its own bare ".NET" dialog while leaving nothing on disk. Log it instead,
        // and say so in plain words: the message loop survives this, so the app stays up.
        Application.ThreadException += (_, e) => ReportRecoverable(e.Exception);

        // The diagnostic switches must never block on a dialog — a modal box would hang a
        // headless run forever. They record faults and fail on them instead. They also must not
        // write settings: the smoke test clicks through switches that save, and a test run has no
        // business rewriting the user's configuration.
        _quiet = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)
                               || a.Equals("--smoketest", StringComparison.OrdinalIgnoreCase)
                               || a.Equals("--uipreview", StringComparison.OrdinalIgnoreCase));
        Settings.SuppressSave = _quiet;

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
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "kyklos-selftest-error.txt"), ex.ToString());
                NativeMethods.HardExit(99);
            }
            return;
        }

        if (args.Any(a => a.Equals("--uipreview", StringComparison.OrdinalIgnoreCase)))
        {
            // Diagnostic: opens the wheel and walks it through idle, running and fade so the
            // wedges, the spinner hub and the caption can all be looked at or screenshotted
            // without converting anything. Deliberately ahead of the single-instance check —
            // previewing alongside a running tray is the whole point, and it must never be
            // mistaken for a file hand-off.
            using var preview = new TrayApp(settings, []);
            preview.ShowWheelPreviewForTest(files.FirstOrDefault());
            var phase = 0;
            using var previewTimer = new System.Windows.Forms.Timer { Interval = 2600 };
            previewTimer.Tick += (_, _) =>
            {
                phase++;
                switch (phase)
                {
                    case 1:
                        preview.BeginPreviewBusyForTest();
                        return;

                    case 2:
                        // End the simulated batch so the fade-out is part of the preview too.
                        preview.EndPreviewBusyForTest();
                        return;

                    default:
                        previewTimer.Stop();
                        preview.ShutdownForTest();
                        return;
                }
            };
            previewTimer.Start();
            Application.Run(preview);
            NativeMethods.HardExit(0);
            return;
        }

        // Both diagnostic switches run ahead of the single-instance check. A smoke test that
        // falls into the "hand the files to the running copy and exit 0" path would report
        // success without having tested anything, which is worse than not having the switch.
        if (args.Any(a => a.Equals("--smoketest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSmokeTest(settings, files);
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

    private static string CrashLogPath => Path.Combine(Settings.DirPath, "crash.log");

    /// <summary>
    /// Boots the real tray UI, walks the paths that are not conversions (floating icon show/hide,
    /// its context menu), runs one genuine conversion through the very code path the wheel uses,
    /// then shuts down the way the Exit menu item does. Every failure mode gets its own exit code,
    /// and a UI-thread exception counts as a failure rather than being quietly logged.
    /// </summary>
    private static void RunSmokeTest(Settings settings, string[] files)
    {
        using var smoke = new TrayApp(settings, []);
        var interactionOk = true;
        var conversionOk = true;
        var menuOk = true;
        var hiddenAfterMenuOk = true;
        var conversionStarted = false;
        var menuExercised = false;
        Exception? fault = null;
        Task<IReadOnlyList<ConvertResult>>? conversion = null;

        using var timer = new System.Windows.Forms.Timer { Interval = 900 };
        timer.Tick += (_, _) =>
        {
            // A fault inside a phase would otherwise skip the rest of this handler — including
            // the shutdown — and leave the run hanging with nothing to show for it.
            try
            {
                if (!conversionStarted && files.Length > 0)
                {
                    conversionStarted = true;
                    conversion = smoke.RunConversionForTest(files[0]);
                    return;
                }

                if (conversion is null || !conversion.IsCompleted) return;

                if (!menuExercised)
                {
                    // The conversion is done; now walk the paths that are not conversions.
                    interactionOk = smoke.ExerciseInteractionForTest();
                    menuOk = smoke.ExerciseDropTargetMenuForTest();
                    menuExercised = true;
                    return;
                }

                // Hiding is deferred to the next message-loop turn, so it is checked now rather
                // than straight after the click.
                hiddenAfterMenuOk = !smoke.DropTargetVisibleForTest;

                var results = conversion.Status == TaskStatus.RanToCompletion ? conversion.Result : [];
                conversionOk = results.Count > 0
                               && results.All(r => r.Success && r.Output is not null && File.Exists(r.Output));

                if (!conversionOk)
                {
                    Console.Error.WriteLine(results.Count == 0
                        ? "smoketest: no conversion result was produced"
                        : $"smoketest: conversion failed: {results[0].Error}");
                }

                timer.Stop();
                smoke.ShutdownForTest();
            }
            catch (Exception ex)
            {
                fault = ex;
                timer.Stop();
                smoke.ShutdownForTest();
            }
        };
        timer.Start();
        Application.Run(smoke);

        if (fault is not null)
        {
            Console.Error.WriteLine($"smoketest: {fault.GetType().Name}: {fault.Message}");
            NativeMethods.HardExit(5);
        }

        if (UiFaultCount > 0)
        {
            Console.Error.WriteLine($"smoketest: {UiFaultCount} UI-thread exception(s) were logged");
            NativeMethods.HardExit(3);
        }

        if (!menuOk || !hiddenAfterMenuOk)
        {
            Console.Error.WriteLine($"smoketest: hiding the floating icon failed (menu={menuOk}, hidden={hiddenAfterMenuOk})");
            NativeMethods.HardExit(4);
        }

        if (!conversionOk)
        {
            NativeMethods.HardExit(2);
        }

        if (!interactionOk)
        {
            Console.Error.WriteLine("smoketest: drop target show/hide/show did not settle");
            NativeMethods.HardExit(1);
        }

        NativeMethods.HardExit(0);
    }

    /// <summary>Appends an exception to the crash log and returns the path it went to.</summary>
    private static string AppendCrashLog(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Settings.DirPath);
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:u}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing more we can do.
        }

        return CrashLogPath;
    }

    /// <summary>Writes an unhandled exception to disk so a silent crash is diagnosable.</summary>
    private static void ReportFatal(Exception? exception)
    {
        var path = AppendCrashLog(exception);

        if (_quiet) return;

        try
        {
            MessageBox.Show(
                $"Kyklos hit an unexpected error and will close.\n\n{exception?.Message}\n\nDetails were written to:\n{path}",
                "Kyklos", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    /// <summary>
    /// A fault raised inside a UI event handler. The message loop carries on, so this is reported
    /// rather than fatal — but it is logged, because a fault that only shows a dialog and leaves
    /// no trace behind is the worst kind to chase.
    /// </summary>
    private static void ReportRecoverable(Exception exception)
    {
        Interlocked.Increment(ref _uiFaults);
        var path = AppendCrashLog(exception);

        if (_quiet) return;

        try
        {
            MessageBox.Show(
                $"Kyklos hit an unexpected error, but is still running.\n\n{exception.Message}\n\nDetails were written to:\n{path}",
                "Kyklos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}
