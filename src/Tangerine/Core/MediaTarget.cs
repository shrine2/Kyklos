namespace Tangerine.Core;

public enum MediaKind
{
    Unknown,
    Video,
    Audio,
    Image,
    Pdf,
    Document,
    Subtitle,
    Delimited,
}

public enum ToolId
{
    Compress,
    StripMetadata,
    TrimResize,
    ExtractAudio,
    MakeGif,
    ToPdf,
}

/// <summary>Per-conversion modifiers chosen on the inner ring of the radial wheel.</summary>
public sealed class ConvertOptions
{
    public bool Compress { get; set; }
    public bool StripMetadata { get; set; }
    public string Quality { get; set; } = Core.Quality.Balanced;
    public TimeSpan? TrimStart { get; set; }
    public TimeSpan? TrimEnd { get; set; }
    public int? ResizeWidth { get; set; }

    public bool HasTrim => TrimStart.HasValue || TrimEnd.HasValue;

    public ConvertOptions Clone() => (ConvertOptions)MemberwiseClone();

    public string Describe()
    {
        var parts = new List<string>();
        if (Compress) parts.Add($"compress/{Quality}");
        if (HasTrim) parts.Add($"trim {Format(TrimStart)}–{Format(TrimEnd)}");
        if (ResizeWidth is { } w) parts.Add($"width {w}px");
        if (StripMetadata) parts.Add("no metadata");
        return parts.Count == 0 ? "standard" : string.Join(", ", parts);
    }

    private static string Format(TimeSpan? t) => t?.ToString(@"mm\:ss\.f") ?? "end";
}

/// <summary>One selectable conversion on the outer ring of the wheel.</summary>
public sealed class MediaTarget
{
    public required string Ext { get; init; }
    public required string Label { get; init; }
    public required MediaKind Output { get; init; }
    public MediaKind Source { get; init; } = MediaKind.Unknown;

    /// <summary>Required ffmpeg encoder, if this target is produced by ffmpeg.</summary>
    public string? Encoder { get; init; }

    /// <summary>Required ffmpeg muxer, if this target is produced by ffmpeg.</summary>
    public string? Muxer { get; init; }

    /// <summary>True when LibreOffice (soffice) is required.</summary>
    public bool NeedsOffice { get; init; }

    /// <summary>True when the Windows Imaging Component codec is required (HEIC/HEIF encode).</summary>
    public bool NeedsWic { get; init; }

    /// <summary>True when the target is a PDF assembled from image(s) by the built-in PDF writer.</summary>
    public bool NeedsPdfWrite { get; init; }

    /// <summary>True when the source is a PDF that must be rasterised (Windows.Data.Pdf).</summary>
    public bool NeedsPdfRender { get; init; }

    /// <summary>True when the source is a PDF whose text is extracted (PdfPig).</summary>
    public bool NeedsPdfText { get; init; }

    /// <summary>True when this is a pure-text conversion handled by <see cref="TextTools"/>.</summary>
    public bool NeedsText { get; init; }

    /// <summary>Builds the ffmpeg argument list (input + codec options); output path is appended by the converter.</summary>
    public Func<string, ConvertOptions, List<string>>? Args { get; init; }

    /// <summary>Seconds of extra work hint, used only for wheel tooltips.</summary>
    public string? Hint { get; init; }

    public bool Available { get; set; } = true;
    public string? UnavailableReason { get; set; }
}

public sealed record ToolItem(ToolId Id, string Label, bool Toggle, bool Active);

/// <summary>Helpers shared by every ffmpeg argument builder.</summary>
internal static class FfArgs
{
    public static string Time(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.fff");

    /// <summary>-ss goes before -i (fast seek); duration is emitted after.</summary>
    public static void AddInput(List<string> args, ConvertOptions options, string source)
    {
        if (options.TrimStart is { } start && start > TimeSpan.Zero)
        {
            args.Add("-ss");
            args.Add(Time(start));
        }

        args.Add("-i");
        args.Add(source);

        if (options.TrimEnd is { } end)
        {
            var duration = end - (options.TrimStart ?? TimeSpan.Zero);
            if (duration > TimeSpan.Zero)
            {
                args.Add("-t");
                args.Add(Time(duration));
            }
        }
    }

    public static void AddMetadata(List<string> args, ConvertOptions options)
    {
        if (options.StripMetadata)
        {
            args.Add("-map_metadata");
            args.Add("-1");
            args.Add("-map_chapters");
            args.Add("-1");
        }
    }

    /// <summary>Scale filter for video (even dimensions) or images.</summary>
    public static string ScaleFilter(int width, bool evenDimensions) =>
        $"scale={width}:{(evenDimensions ? "-2" : "-1")}";
}
