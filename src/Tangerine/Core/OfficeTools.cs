using System.Diagnostics;
using System.Text;

namespace Tangerine.Core;

/// <summary>
/// Document conversion through a headless LibreOffice instance.
/// soffice can only ever write one profile at a time and refuses to run two instances,
/// so calls are serialised through a single semaphore.
/// </summary>
public static class OfficeTools
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<string> ConvertAsync(string source, string targetExt, string finalOutput, CancellationToken ct)
    {
        var soffice = Toolchain.Soffice
                      ?? throw new InvalidOperationException("LibreOffice was not found. Install it with: winget install TheDocumentFoundation.LibreOffice");

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        var workDir = Paths.NewTempDir();
        try
        {
            var profileDir = Path.Combine(Paths.TempRoot, "lo-profile");
            Directory.CreateDirectory(profileDir);

            var convertTo = BuildConvertTo(targetExt);

            var startInfo = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            foreach (var argument in new[]
                     {
                         "--headless",
                         "--norestore",
                         "--nolockcheck",
                         "--nodefault",
                         "--nofirststartwizard",
                         "-env:UserInstallation=" + new Uri(profileDir).AbsoluteUri,
                         "--convert-to", convertTo,
                         "--outdir", workDir,
                         source,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Could not start LibreOffice.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var registration = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            });

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var produced = Directory.EnumerateFiles(workDir).FirstOrDefault();
            if (produced is null)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(
                    $"LibreOffice produced no output for '{Path.GetFileName(source)}'. {Truncate(detail)}");
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(finalOutput));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.Move(produced, finalOutput, overwrite: true);
            return finalOutput;
        }
        finally
        {
            Gate.Release();
            Paths.TryDeleteTree(workDir);
        }
    }

    /// <summary>LibreOffice needs an explicit filter for UTF-8 text and Word output.</summary>
    private static string BuildConvertTo(string targetExt) => targetExt.ToLowerInvariant() switch
    {
        "txt" => "txt:Text (encoded):UTF8",
        "html" => "html:XHTML Writer File:UTF8",
        "docx" => "docx:MS Word 2007 XML",
        _ => targetExt.ToLowerInvariant(),
    };

    private static string Truncate(string text)
    {
        text = text.Trim();
        if (text.Length <= 400) return text;
        var builder = new StringBuilder(text[..400]);
        builder.Append('…');
        return builder.ToString();
    }
}
