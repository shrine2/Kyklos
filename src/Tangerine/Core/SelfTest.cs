using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace Tangerine.Core;

/// <summary>
/// Headless verification of the conversion engine: generates sample media, pushes it through
/// every code path (ffmpeg, built-in PDF writer, Windows PDF renderer, PdfPig text extraction)
/// and writes a report. Run with: Tangerine.exe --selftest &lt;output-folder&gt;
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string workDirectory, string only = "all")
    {
        var report = new List<string>();
        var failures = 0;

        bool Wants(string section) => only is "all" || string.Equals(only, section, StringComparison.OrdinalIgnoreCase);
        var settings = new Settings
        {
            OutputMode = OutputModes.Beside,
            OverwriteMode = OverwriteModes.Overwrite,
            Quality = Quality.Balanced,
        };

        Paths.TryDeleteTree(workDirectory);
        var dir = Paths.EnsureDirectory(workDirectory);

        void Line(string text) => report.Add(text);

        Line($"Tangerine self-test — {DateTime.Now:u}");
        Line($"ffmpeg: {(Toolchain.FfmpegReady ? Toolchain.Ffmpeg : "NOT FOUND")}");
        Line(Toolchain.FfmpegVersion);
        Line($"LibreOffice: {(Toolchain.OfficeReady ? Toolchain.Soffice : "not installed")}");
        Line($"encoders: {Toolchain.Caps.Encoders.Count}, muxers: {Toolchain.Caps.Muxers.Count}");
        Line(new string('-', 78));

        async Task Check(string label, string source, string targetExt)
        {
            var kind = FormatCatalog.DetectKind(source);
            var target = FormatCatalog.FindTarget(kind, targetExt);
            if (target is null)
            {
                failures++;
                Line($"FAIL  {label,-34} no target defined");
                return;
            }

            if (!target.Available)
            {
                Line($"SKIP  {label,-34} unavailable: {target.UnavailableReason}");
                return;
            }

            var options = new ConvertOptions { Quality = Quality.Balanced };
            var result = await Converter.ExecuteAsync(source, target, options, settings, CancellationToken.None);

            if (result.Success && result.Output is not null && File.Exists(result.Output))
            {
                Line($"PASS  {label,-34} {Path.GetFileName(result.Output),-26} {Paths.HumanSize(result.Bytes)}");
            }
            else
            {
                failures++;
                Line($"FAIL  {label,-34} {result.Error}");
            }
        }

        // Text conversions are judged on their contents, not just on a file existing.
        string TryRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
            catch { return string.Empty; }
        }

        void Verify(bool condition, string label, string detail)
        {
            if (condition) Line($"PASS  {label,-34} content verified");
            else { failures++; Line($"FAIL  {label,-34} {detail}"); }
        }

        // ---------------------------------------------------------------- fixtures

        var pngPath = Path.Combine(dir, "sample-image.png");
        var png2Path = Path.Combine(dir, "sample-page2.png");
        if (Wants("images") || Wants("pdf") || Wants("pdfwrite") || Wants("pdfrender") || Wants("pdftext") || Wants("pdfwr"))
        {
            CreateSampleImage(pngPath, 640, 400);
            CreateSampleImage(png2Path, 500, 700);
        }

        var videoPath = Path.Combine(dir, "sample-video.mp4");
        if (Toolchain.FfmpegReady && Wants("video"))
        {
            RunProcess(Toolchain.Ffmpeg!,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=15",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-t", "3", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", videoPath,
            ]);
        }

        var audioPath = Path.Combine(dir, "sample-audio.wav");
        if (Toolchain.FfmpegReady && Wants("audio"))
        {
            RunProcess(Toolchain.Ffmpeg!,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=44100",
                "-t", "2", audioPath,
            ]);
        }

        // ---------------------------------------------------------------- image paths

        if (Wants("images"))
        {
            Line(string.Empty);
            Line("images");
            if (File.Exists(pngPath))
            {
                foreach (var ext in new[] { "jpg", "webp", "avif", "tiff", "bmp", "gif", "heic", "pdf" })
                    await Check($"png -> {ext}", pngPath, ext);

                // compress + resize + metadata strip in one go
                var compressed = await Converter.ExecuteAsync(pngPath,
                    FormatCatalog.FindTarget(MediaKind.Image, "jpg")!,
                    new ConvertOptions { Compress = true, Quality = Quality.Fast, ResizeWidth = 320, StripMetadata = true },
                    settings, CancellationToken.None);
                if (compressed.Success) Line($"PASS  png -> jpg (compress+resize)      {Path.GetFileName(compressed.Output)}  {Paths.HumanSize(compressed.Bytes)}");
                else { failures++; Line($"FAIL  png -> jpg (compress+resize)      {compressed.Error}"); }
            }
        }

        // ---------------------------------------------------------------- video paths

        if (Wants("video"))
        {
            Line(string.Empty);
            Line("video");
            if (File.Exists(videoPath))
            {
                foreach (var ext in new[] { "mp4", "mkv", "mov", "webm", "avi", "wmv", "gif", "mp3", "m4a", "wav", "flac" })
                    await Check($"mp4 -> {ext}", videoPath, ext);

                var trimmed = await Converter.ExecuteAsync(videoPath,
                    FormatCatalog.FindTarget(MediaKind.Video, "mp4")!,
                    new ConvertOptions { TrimStart = TimeSpan.FromSeconds(1), TrimEnd = TimeSpan.FromSeconds(2), ResizeWidth = 160 },
                    settings, CancellationToken.None);
                if (trimmed.Success) Line($"PASS  mp4 -> mp4 (trim 1s+resize)        {Path.GetFileName(trimmed.Output)}  {Paths.HumanSize(trimmed.Bytes)}");
                else { failures++; Line($"FAIL  mp4 -> mp4 (trim 1s+resize)        {trimmed.Error}"); }
            }
            else
            {
                Line("video: skipped (no fixture)");
            }
        }

        // ---------------------------------------------------------------- audio paths

        if (Wants("audio"))
        {
            Line(string.Empty);
            Line("audio");
            if (File.Exists(audioPath))
            {
                foreach (var ext in new[] { "mp3", "m4a", "flac", "ogg", "opus", "aiff", "wma" })
                    await Check($"wav -> {ext}", audioPath, ext);
            }
        }

        // ---------------------------------------------------------------- PDF paths

        if (Wants("pdf") || Wants("pdfwrite") || Wants("pdfrender") || Wants("pdftext") || Wants("pdfwr"))
        {
            Line(string.Empty);
            Line("pdf");
            var pdfPath = Path.Combine(dir, "built.pdf");
            Paths.TryDeleteFile(pdfPath);
            try
            {
                PdfTools.ImagesToPdf([pngPath, png2Path], pdfPath);
                Line($"PASS  images -> pdf (2 pages)            {Path.GetFileName(pdfPath)}  {Paths.HumanSize(new FileInfo(pdfPath).Length)}");
            }
            catch (Exception ex)
            {
                failures++;
                Line($"FAIL  images -> pdf                      {ex.Message}");
            }

            if (File.Exists(pdfPath) && (Wants("pdf") || Wants("pdfrender") || Wants("pdfwr")))
            {
                await Check("pdf -> png", pdfPath, "png");
                await Check("pdf -> jpg", pdfPath, "jpg");
            }

            if (File.Exists(pdfPath) && (Wants("pdf") || Wants("pdftext")))
            {
                await Check("pdf -> txt", pdfPath, "txt");
            }
        }

        // ---------------------------------------------------------------- subtitles and tables

        if (Wants("text"))
        {
            Line(string.Empty);
            Line("text");

            var srtPath = Path.Combine(dir, "subs-a.srt");
            File.WriteAllText(srtPath,
                "1\n00:00:01,000 --> 00:00:03,500\nHello <i>world</i>\n\n" +
                "2\n00:00:04,000 --> 00:00:06,250\nSecond cue\n", Encoding.UTF8);

            await Check("srt -> vtt", srtPath, "vtt");
            await Check("srt -> txt", srtPath, "txt");

            var vttText = TryRead(Path.Combine(dir, "subs-a.vtt"));
            if (vttText.Length == 0) { failures++; Line("FAIL  vtt content                        not written"); }
            else
            {
                Verify(vttText.StartsWith("WEBVTT", StringComparison.Ordinal), "vtt header", "missing the WEBVTT header");
                Verify(vttText.Contains("00:00:01.000 --> 00:00:03.500", StringComparison.Ordinal), "vtt timestamps", "timestamps were not converted to a dot separator");
            }

            var txtText = TryRead(Path.Combine(dir, "subs-a.txt"));
            Verify(txtText.Contains("Hello world", StringComparison.Ordinal), "txt strips markup", "inline markup was not stripped");

            var vttPath = Path.Combine(dir, "subs-b.vtt");
            File.WriteAllText(vttPath,
                "WEBVTT\n\nNOTE this block is ignored\n\n00:00:02.500 --> 00:00:05.000\nFrom WebVTT\n", Encoding.UTF8);

            await Check("vtt -> srt", vttPath, "srt");
            var backToSrt = TryRead(Path.Combine(dir, "subs-b.srt"));
            Verify(backToSrt.Contains("00:00:02,500 --> 00:00:05,000", StringComparison.Ordinal), "srt timestamps", "timestamps were not converted to a comma separator");
            Verify(!backToSrt.Contains("NOTE", StringComparison.Ordinal), "srt drops notes", "the VTT NOTE block leaked into the SRT");

            var csvPath = Path.Combine(dir, "table-a.csv");
            File.WriteAllText(csvPath,
                "name,note,size\n\"Smith, John\",\"He said \"\"hi\"\"\",42\nplain,text,7\n", Encoding.UTF8);

            await Check("csv -> tsv", csvPath, "tsv");
            var tsvText = TryRead(Path.Combine(dir, "table-a.tsv"));
            Verify(tsvText.Contains("Smith, John\t", StringComparison.Ordinal), "tsv keeps commas", "an embedded comma was lost or re-quoted");
            Verify(tsvText.Contains("\"He said \"\"hi\"\"\"\t", StringComparison.Ordinal), "tsv escapes quotes", "escaped quotes were not preserved");

            var tsvPath = Path.Combine(dir, "table-b.tsv");
            File.WriteAllText(tsvPath, "a\tb\tc\n1\t2\t3\n", Encoding.UTF8);

            await Check("tsv -> csv", tsvPath, "csv");
            Verify(TryRead(Path.Combine(dir, "table-b.csv")).StartsWith("a,b,c", StringComparison.Ordinal), "csv delimiter", "tabs were not converted to commas");
        }

        // ---------------------------------------------------------------- documents

        if (Wants("docs"))
        {
            Line(string.Empty);
            Line("documents");
            if (Toolchain.OfficeReady)
            {
                var txtPath = Path.Combine(dir, "sample.txt");
                File.WriteAllText(txtPath, "Tangerine document conversion test.\r\nSecond line.\r\n", Encoding.UTF8);
                foreach (var ext in new[] { "pdf", "docx", "odt", "html", "rtf", "epub" })
                    await Check($"txt -> {ext}", txtPath, ext);
            }
            else
            {
                Line("SKIP  all document targets               LibreOffice is not installed");
            }
        }

        // ---------------------------------------------------------------- summary

        Line(new string('-', 78));
        Line(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");

        var reportPath = Path.Combine(dir, "selftest-report.txt");
        File.WriteAllLines(reportPath, report);
        return failures;
    }

    private static void CreateSampleImage(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new LinearGradientBrush(
                       new Rectangle(0, 0, width, height),
                       Color.FromArgb(255, 255, 138, 61),
                       Color.FromArgb(255, 40, 30, 60),
                       45f))
            {
                g.FillRectangle(background, 0, 0, width, height);
            }

            using var font = new Font("Segoe UI", Math.Max(14, width / 14f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            g.DrawString("Tangerine", font, text, width * 0.08f, height * 0.4f);
            g.DrawString($"{width}×{height}", font, text, width * 0.08f, height * 0.58f);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static void RunProcess(string exe, string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in args) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)!;
            process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
        }
        catch
        {
            // Fixture generation is best effort; the checks report the real outcome.
        }
    }
}
