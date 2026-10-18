using System.Globalization;
using System.Text;

namespace Kyklos.Core;

/// <summary>
/// Conversions that are pure text and need no external tool at all: subtitles
/// (SRT/VTT) and delimited tables (CSV/TSV). These keep working even when ffmpeg
/// and LibreOffice are missing.
/// </summary>
public static class TextTools
{
    public static void Convert(string source, string targetExt, string output)
    {
        var text = ReadText(source);

        var result = targetExt.ToLowerInvariant() switch
        {
            "srt" => ToSrt(text),
            "vtt" => ToVtt(text),
            "txt" => ToPlainText(text),
            "csv" => Emit(ParseTable(text), ','),
            "tsv" => Emit(ParseTable(text), '\t'),
            _ => throw new InvalidOperationException($"There is no text conversion to '.{targetExt}'."),
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(output, result, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ReadText(string path)
    {
        // ReadAllText honours a BOM; normalise line endings so the emitters stay simple.
        var text = File.ReadAllText(path);
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // ---------------------------------------------------------------- subtitles

    private sealed record Cue(TimeSpan Start, TimeSpan End, List<string> Lines);

    private static string ToSrt(string text)
    {
        var cues = ParseCues(text);
        if (cues.Count == 0) throw new InvalidOperationException("No subtitle cues were found in that file.");

        var builder = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            builder.Append(i + 1).Append('\n');
            builder.Append(Stamp(cues[i].Start, ',')).Append(" --> ").Append(Stamp(cues[i].End, ',')).Append('\n');
            foreach (var line in cues[i].Lines) builder.Append(line).Append('\n');
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static string ToVtt(string text)
    {
        var cues = ParseCues(text);
        if (cues.Count == 0) throw new InvalidOperationException("No subtitle cues were found in that file.");

        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
        {
            builder.Append(Stamp(cue.Start, '.')).Append(" --> ").Append(Stamp(cue.End, '.')).Append('\n');
            foreach (var line in cue.Lines) builder.Append(line).Append('\n');
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static string ToPlainText(string text)
    {
        var cues = ParseCues(text);
        if (cues.Count == 0)
        {
            // Not a subtitle file: hand back the text with markup and blank runs removed.
            return Strip(text);
        }

        var builder = new StringBuilder();
        foreach (var cue in cues)
        {
            foreach (var line in cue.Lines) builder.Append(Strip(line)).Append('\n');
            builder.Append('\n');
        }
        return builder.ToString().TrimEnd() + "\n";
    }

    private static string Stamp(TimeSpan t, char separator) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{separator}{t.Milliseconds:000}";

    private static List<Cue> ParseCues(string text)
    {
        var cues = new List<Cue>();
        var lines = text.Split('\n');
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) { index++; continue; }

            if (line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) { index++; continue; }

            if (line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
            {
                while (index < lines.Length && lines[index].Trim().Length > 0) index++;
                continue;
            }

            // An SRT cue number on its own line, followed by the timing line.
            if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                && index + 1 < lines.Length
                && lines[index + 1].Contains("-->", StringComparison.Ordinal))
            {
                index++;
                line = lines[index].Trim();
            }

            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) { index++; continue; }

            var startRaw = line[..arrow].Trim();
            // VTT allows cue settings after the end timestamp; SRT does not.
            var endRaw = line[(arrow + 3)..].Trim().Split(' ')[0];
            index++;

            var body = new List<string>();
            while (index < lines.Length && lines[index].Trim().Length > 0)
            {
                body.Add(lines[index].TrimEnd());
                index++;
            }

            if (TryParseStamp(startRaw, out var start) && TryParseStamp(endRaw, out var end) && end >= start)
                cues.Add(new Cue(start, end, body));
        }

        return cues;
    }

    private static bool TryParseStamp(string raw, out TimeSpan value)
    {
        value = default;
        var parts = raw.Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3) return false;

        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return false;
        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return false;

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)) return false;

        value = new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Drops inline subtitle markup such as &lt;i&gt; tags and ASS override blocks.</summary>
    private static string Strip(string line)
    {
        var builder = new StringBuilder();
        var depth = 0;
        var inBrace = false;

        foreach (var ch in line)
        {
            if (inBrace)
            {
                if (ch == '}') inBrace = false;
                continue;
            }

            if (ch == '{') { inBrace = true; continue; }
            if (ch == '<') { depth++; continue; }
            if (ch == '>' && depth > 0) { depth--; continue; }
            if (depth == 0) builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    // ---------------------------------------------------------------- delimited tables

    private static List<string[]> ParseTable(string text)
    {
        var delimiter = DetectDelimiter(text);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(ch);
                continue;
            }

            if (ch == '"') { quoted = true; continue; }
            if (ch == delimiter) { row.Add(field.ToString()); field.Clear(); continue; }

            if (ch == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
                continue;
            }

            field.Append(ch);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            return line.Contains('\t') ? '\t' : ',';
        }
        return ',';
    }

    private static string Emit(List<string[]> rows, char delimiter)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) builder.Append(delimiter);
                builder.Append(Quote(row[i], delimiter));
            }
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static string Quote(string field, char delimiter)
    {
        var needs = field.Contains(delimiter) || field.Contains('"') || field.Contains('\n');
        return needs ? '"' + field.Replace("\"", "\"\"") + '"' : field;
    }
}
