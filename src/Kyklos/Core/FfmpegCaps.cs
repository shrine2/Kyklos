using System.Text.RegularExpressions;

namespace Kyklos.Core;

/// <summary>
/// Format capabilities discovered from the local ffmpeg build.
/// The radial wheel only offers targets this build can actually produce, so the app
/// degrades gracefully on minimal ffmpeg builds instead of failing at runtime.
/// </summary>
public sealed class FfmpegCaps
{
    public HashSet<string> Encoders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Decoders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Muxers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasEncoder(params string[] names) => names.Any(Encoders.Contains);
    public bool HasDecoder(params string[] names) => names.Any(Decoders.Contains);
    public bool HasMuxer(params string[] names) => names.Any(Muxers.Contains);

    // " V....D libx264              libx264 H.264 ..."  ->  libx264
    private static readonly Regex Flags6 = new(@"^\s*[A-Z.]{6}\s+(\S+)", RegexOptions.Compiled);
    // "  E  mp4    MP4 (MPEG-4 Part 14)"                ->  mp4
    private static readonly Regex Flags3 = new(@"^\s*[A-Z.]{1,3}\s+(\S+)", RegexOptions.Compiled);

    public static FfmpegCaps Parse(string encodersOutput, string decodersOutput, string muxersOutput)
    {
        var caps = new FfmpegCaps();
        Fill(caps.Encoders, encodersOutput, Flags6);
        Fill(caps.Decoders, decodersOutput, Flags6);
        Fill(caps.Muxers, muxersOutput, Flags3);
        return caps;
    }

    private static void Fill(HashSet<string> set, string text, Regex pattern)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Contains(':')) continue;      // "Encoders:" headers
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('-')) continue; // legend rows
            if (line.Contains(" = ")) continue;    // legend rows

            var match = pattern.Match(line);
            if (!match.Success) continue;

            var name = match.Groups[1].Value;
            if (name.Length < 2 || name[0] == '=') continue;
            set.Add(name);
        }
    }
}
