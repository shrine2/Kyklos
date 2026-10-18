using System.Diagnostics;
using System.Text;

namespace Kyklos.Core;

public sealed record ConvertResult(
    bool Success,
    bool Skipped,
    string Source,
    string? Output,
    string? Error,
    long Bytes)
{
    public string SourceName => Path.GetFileName(Source);
}

/// <summary>Runs a single conversion, dispatching to ffmpeg, LibreOffice, the PDF writer or WIC.</summary>
public static class Converter
{
    public static async Task<ConvertResult> ExecuteAsync(
        string source,
        MediaTarget target,
        ConvertOptions options,
        Settings settings,
        CancellationToken ct)
    {
        var (output, shouldSkip) = Paths.ResolveOutput(source, target.Ext, settings);
        if (shouldSkip)
            return new ConvertResult(true, true, source, output, "skipped (already exists)", 0);

        // Re-encoding a file into its own format with "overwrite" selected resolves the output to the
        // source path, which ffmpeg refuses to read and write at once. Stage next to the original and
        // swap it in afterwards instead.
        var inPlace = string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        var writeTarget = inPlace ? StagingPath(source, target.Ext) : output;

        try
        {
            IReadOnlyList<string> produced;

            if (target.NeedsOffice)
            {
                produced = [await OfficeTools.ConvertAsync(source, target.Ext, writeTarget, ct).ConfigureAwait(false)];
            }
            else if (target.NeedsPdfWrite)
            {
                produced = await Task.Run(() => PdfTools.ImagesToPdf([source], writeTarget), ct).ConfigureAwait(false);
            }
            else if (target.NeedsPdfRender)
            {
                produced = await PdfTools.RenderPagesAsync(source, writeTarget, target.Ext, ct).ConfigureAwait(false);
            }
            else if (target.NeedsPdfText)
            {
                var path = writeTarget;
                await Task.Run(() => PdfTools.ExtractText(source, path), ct).ConfigureAwait(false);
                produced = [writeTarget];
            }
            else if (target.NeedsText)
            {
                var path = writeTarget;
                await Task.Run(() => TextTools.Convert(source, target.Ext, path), ct).ConfigureAwait(false);
                produced = [writeTarget];
            }
            else if (target.NeedsWic)
            {
                var path = writeTarget;
                await Task.Run(() =>
                {
                    if (!WicImage.TryConvert(source, path, target.Ext, out var error))
                        throw new InvalidOperationException(error ?? "Windows codec conversion failed.");
                }, ct).ConfigureAwait(false);
                produced = [writeTarget];
            }
            else
            {
                await RunFfmpegAsync(source, target, writeTarget, options, ct).ConfigureAwait(false);
                produced = [writeTarget];
            }

            if (inPlace)
            {
                if (produced.Count != 1)
                    throw new InvalidOperationException("In-place conversion produced an unexpected number of files.");

                File.Move(writeTarget, output, overwrite: true);
                produced = [output];
            }

            long bytes = 0;
            foreach (var path in produced)
            {
                if (File.Exists(path)) bytes += new FileInfo(path).Length;
            }

            var primary = produced.FirstOrDefault();
            return new ConvertResult(true, false, source, primary, null, bytes);
        }
        catch (OperationCanceledException)
        {
            Paths.TryDeleteFile(writeTarget);
            throw;
        }
        catch (Exception ex)
        {
            if (inPlace) Paths.TryDeleteFile(writeTarget);
            return new ConvertResult(false, false, source, null, ex.Message, 0);
        }
    }

    /// <summary>A hidden sibling path used when the real destination is the source file itself.</summary>
    private static string StagingPath(string source, string ext)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var name = Path.GetFileNameWithoutExtension(source);
        return Path.Combine(directory, $".{name}.kyklos-{Guid.NewGuid():N}.{ext.TrimStart('.')}");
    }

    private static async Task RunFfmpegAsync(
        string source, MediaTarget target, string output, ConvertOptions options, CancellationToken ct)
    {
        var ffmpeg = Toolchain.Ffmpeg
                     ?? throw new InvalidOperationException("ffmpeg was not found. Install it with: winget install Gyan.FFmpeg");

        if (target.Args is null)
            throw new InvalidOperationException($"No conversion recipe exists for {target.Label}.");

        string? temporary = null;
        var input = source;

        try
        {
            if (NeedsWicDecode(source))
            {
                var tempDir = Paths.NewTempDir();
                temporary = Path.Combine(tempDir, "decoded.png");
                WicImage.Convert(source, temporary, "png");
                input = temporary;
            }

            var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" };
            args.AddRange(target.Args(input, options));
            args.Add(output);

            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var startInfo = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in args) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Could not start ffmpeg.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var registration = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            });

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg failed (exit {process.ExitCode}): {Summarise(stderr)}");

            if (!File.Exists(output))
                throw new InvalidOperationException($"ffmpeg finished but produced no file. {Summarise(stderr)}");
        }
        finally
        {
            if (temporary is not null) Paths.TryDeleteTree(Path.GetDirectoryName(temporary));
        }
    }

    /// <summary>Formats ffmpeg cannot decode on this build are routed through the Windows codec first.</summary>
    private static bool NeedsWicDecode(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".heic" or ".heif" => !Toolchain.Caps.HasDecoder("heif", "heic"),
        ".jxl" => !Toolchain.Caps.HasDecoder("jxl", "libjxl"),
        ".avif" => !Toolchain.Caps.HasDecoder("av1"),
        _ => false,
    };

    private static string Summarise(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "no diagnostic output.";
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        var text = string.Join(" | ", lines.TakeLast(3));
        return text.Length <= 400 ? text : text[^400..];
    }
}

/// <summary>Progress report for a multi-file conversion run.</summary>
public sealed record BatchProgress(int Done, int Total, string Source, ConvertResult? Result);

/// <summary>Converts a selection of files, reporting progress and honouring cancellation.</summary>
public static class BatchConverter
{
    public static async Task<IReadOnlyList<ConvertResult>> RunAsync(
        IReadOnlyList<string> sources,
        MediaTarget target,
        ConvertOptions options,
        Settings settings,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<ConvertResult>(sources.Count);
        var total = sources.Count;

        for (var index = 0; index < total; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[index];
            progress?.Report(new BatchProgress(index, total, source, null));

            var result = await Converter.ExecuteAsync(source, target, options, settings, ct).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new BatchProgress(index + 1, total, source, result));
        }

        return results;
    }
}
