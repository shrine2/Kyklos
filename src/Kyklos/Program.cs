using Kyklos.Core;
using Kyklos.Interop;

namespace Kyklos;

internal static class Program
{
    private const string MutexName = @"Local\Kyklos.SingleInstance";

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
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "kyklos-selftest-error.txt"), ex.ToString());
                NativeMethods.HardExit(99);
            }
            return;
        }

        if (args.Any(a => a.Equals("--uipreview", StringComparison.OrdinalIgnoreCase)))
        {
            // Diagnostic: opens the wheel and drives it through a simulated batch so the
            // running state (spinner hub, caption, fade-out) can be looked at or screenshotted
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
                if (phase == 1)
                {
                    // End the simulated batch so the fade-out is part of the preview too.
                    preview.EndPreviewBusyForTest();
                    return;
                }

                previewTimer.Stop();
                preview.ShutdownForTest();
            };
            previewTimer.Start();
            Application.Run(preview);
            NativeMethods.HardExit(0);
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
            // Boots the real tray UI, pokes the interaction toggles, runs one real conversion
            // through the wheel's own code path, then shuts down the way the Exit menu item
            // does. Proves the GUI starts, reconfigures, converts and tears down without fault.
            using var smoke = new TrayApp(settings, []);
            var interactionOk = true;
            var conversionOk = true;
            var conversionStarted = false;
            Task<IReadOnlyList<ConvertResult>>? conversion = null;

            using var timer = new System.Windows.Forms.Timer { Interval = 900 };
            timer.Tick += (_, _) =>
            {
                if (!conversionStarted && files.Length > 0)
                {
                    conversionStarted = true;
                    conversion = smoke.RunConversionForTest(files[0]);
                    return;
                }

                if (conversion is null || !conversion.IsCompleted) return;

                timer.Stop();
                interactionOk = smoke.ExerciseInteractionForTest();

                var results = conversion.Status == TaskStatus.RanToCompletion ? conversion.Result : [];
                conversionOk = results.Count > 0
                               && results.All(r => r.Success && r.Output is not null && File.Exists(r.Output));

                if (!conversionOk)
                {
                    Console.Error.WriteLine(results.Count == 0
                        ? "smoketest: no conversion result was produced"
                        : $"smoketest: conversion failed: {results[0].Error}");
                }

                smoke.ShutdownForTest();
            };
            timer.Start();
            Application.Run(smoke);

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
                $"Kyklos hit an unexpected error and will close.\n\n{exception?.Message}\n\nDetails were written to:\n{Path.Combine(Settings.DirPath, "crash.log")}",
                "Kyklos", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}
