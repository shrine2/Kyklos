namespace Tangerine.Core;

/// <summary>
/// Maps a source file to the conversions and tools the wheel should offer.
/// Availability is resolved against the live ffmpeg capability probe and the
/// presence of LibreOffice / the Windows HEIC codec, so unsupported options are
/// shown greyed out with a reason instead of silently failing.
/// </summary>
public static class FormatCatalog
{
    private static readonly Dictionary<string, MediaKind> KindMap = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".mpg", ".mpeg",
        ".flv", ".ts", ".mts", ".m2ts", ".vob", ".ogv", ".3gp", ".divx", ".rm", ".rmvb",
    ];

    private static readonly string[] AudioExtensions =
    [
        ".mp3", ".m4a", ".m4b", ".wav", ".flac", ".ogg", ".oga", ".opus", ".aiff", ".aif",
        ".wma", ".aac", ".alac", ".ape", ".mka", ".ac3", ".dts", ".amr", ".mp2", ".wv",
    ];

    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".webp", ".heic", ".heif", ".tif", ".tiff",
        ".bmp", ".gif", ".avif", ".ico", ".jxl", ".tga", ".dds", ".psd", ".exr", ".hdr",
    ];

    private static readonly string[] PdfExtensions = [".pdf"];

    private static readonly string[] SubtitleExtensions = [".srt", ".vtt"];

    private static readonly string[] DelimitedExtensions = [".csv", ".tsv"];

    private static readonly string[] DocumentExtensions =
    [
        ".docx", ".doc", ".docm", ".odt", ".rtf", ".txt", ".md", ".html", ".htm", ".epub",
        ".pptx", ".ppt", ".odp", ".xlsx", ".xls", ".ods", ".csv", ".xml", ".json", ".log",
        ".pages", ".numbers", ".key", ".wpd", ".wps", ".pages",
    ];

    static FormatCatalog()
    {
        foreach (var ext in VideoExtensions) KindMap[ext] = MediaKind.Video;
        foreach (var ext in AudioExtensions) KindMap[ext] = MediaKind.Audio;
        foreach (var ext in ImageExtensions) KindMap[ext] = MediaKind.Image;
        foreach (var ext in PdfExtensions) KindMap[ext] = MediaKind.Pdf;
        foreach (var ext in DocumentExtensions) KindMap[ext] = MediaKind.Document;
        foreach (var ext in SubtitleExtensions) KindMap[ext] = MediaKind.Subtitle;
        foreach (var ext in DelimitedExtensions) KindMap[ext] = MediaKind.Delimited;
    }

    /// <summary>True when the wheel has anything at all to offer for these files.</summary>
    public static bool CanHandle(string path) => DetectKind(path) != MediaKind.Unknown;

    public static MediaKind DetectKind(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && KindMap.TryGetValue(ext, out var kind) ? kind : MediaKind.Unknown;
    }

    /// <summary>Best-effort kind for a mixed selection: the first recognisable file wins.</summary>
    public static MediaKind DetectKind(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var kind = DetectKind(path);
            if (kind != MediaKind.Unknown) return kind;
        }
        return MediaKind.Unknown;
    }

    /// <summary>
    /// Targets offered for a source kind. When a sample path is supplied, its own format is
    /// dropped from the ring — matching the macOS app, which never offers to convert a file
    /// into the format it already is.
    /// </summary>
    public static List<MediaTarget> TargetsFor(MediaKind source, string? samplePath = null)
    {
        var list = source switch
        {
            MediaKind.Video => VideoTargets(),
            MediaKind.Audio => AudioTargets(),
            MediaKind.Image => ImageTargets(),
            MediaKind.Pdf => PdfTargets(),
            MediaKind.Document => DocumentTargets(),
            MediaKind.Subtitle => SubtitleTargets(),
            MediaKind.Delimited => DelimitedTargets(),
            _ => [],
        };

        if (samplePath is not null)
        {
            var own = Path.GetExtension(samplePath).TrimStart('.');
            if (own.Length > 0)
                list.RemoveAll(t => string.Equals(t.Ext, own, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var target in list) ApplyAvailability(target);
        return list;
    }

    public static MediaTarget? FindTarget(MediaKind source, string ext)
        => TargetsFor(source).FirstOrDefault(t => string.Equals(t.Ext, ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    public static List<ToolItem> ToolsFor(MediaKind source, ConvertOptions options)
    {
        var tools = new List<ToolItem>();

        switch (source)
        {
            case MediaKind.Video:
                tools.Add(new ToolItem(ToolId.Compress, options.Compress ? "COMPRESS ✓" : "COMPRESS", true, options.Compress));
                tools.Add(new ToolItem(ToolId.TrimResize, options.HasTrim || options.ResizeWidth is not null ? "TRIM/SIZE ✓" : "TRIM/SIZE", false, options.HasTrim || options.ResizeWidth is not null));
                tools.Add(new ToolItem(ToolId.StripMetadata, options.StripMetadata ? "NO META ✓" : "NO META", true, options.StripMetadata));
                tools.Add(new ToolItem(ToolId.ExtractAudio, "→ MP3", false, false));
                tools.Add(new ToolItem(ToolId.MakeGif, "→ GIF", false, false));
                break;

            case MediaKind.Audio:
                tools.Add(new ToolItem(ToolId.Compress, options.Compress ? "COMPRESS ✓" : "COMPRESS", true, options.Compress));
                tools.Add(new ToolItem(ToolId.TrimResize, options.HasTrim ? "TRIM ✓" : "TRIM", false, options.HasTrim));
                tools.Add(new ToolItem(ToolId.StripMetadata, options.StripMetadata ? "NO META ✓" : "NO META", true, options.StripMetadata));
                break;

            case MediaKind.Image:
                tools.Add(new ToolItem(ToolId.Compress, options.Compress ? "COMPRESS ✓" : "COMPRESS", true, options.Compress));
                tools.Add(new ToolItem(ToolId.TrimResize, options.ResizeWidth is not null ? "RESIZE ✓" : "RESIZE", false, options.ResizeWidth is not null));
                tools.Add(new ToolItem(ToolId.StripMetadata, options.StripMetadata ? "NO META ✓" : "NO META", true, options.StripMetadata));
                tools.Add(new ToolItem(ToolId.ToPdf, "→ PDF", false, false));
                break;
        }

        return tools;
    }

    // ---------------------------------------------------------------- target sets

    private static List<MediaTarget> VideoTargets() =>
    [
        new MediaTarget
        {
            Ext = "mp4", Label = "MP4", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "libx264", Muxer = "mp4", Hint = "H.264 + AAC",
            Args = (source, o) =>
            {
                var (crf, preset) = Quality.Video(o);
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                args.AddRange(["-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p",
                               "-c:a", "aac", "-b:a", Quality.AudioBitrate(o), "-movflags", "+faststart"]);
                AddVideoScale(args, o);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        new MediaTarget
        {
            Ext = "mkv", Label = "MKV", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "libx264", Muxer = "matroska", Hint = "H.264 + AAC",
            Args = (source, o) =>
            {
                var (crf, preset) = Quality.Video(o);
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                args.AddRange(["-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-c:a", "aac", "-b:a", Quality.AudioBitrate(o)]);
                AddVideoScale(args, o);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        new MediaTarget
        {
            Ext = "mov", Label = "MOV", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "libx264", Muxer = "mov", Hint = "H.264 + AAC",
            Args = (source, o) =>
            {
                var (crf, preset) = Quality.Video(o);
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                args.AddRange(["-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(), "-pix_fmt", "yuv420p",
                               "-c:a", "aac", "-b:a", Quality.AudioBitrate(o)]);
                AddVideoScale(args, o);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        new MediaTarget
        {
            Ext = "webm", Label = "WebM", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "libvpx-vp9", Muxer = "webm", Hint = "VP9 + Opus",
            Args = (source, o) =>
            {
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                var crf = o.Compress ? "36" : "31";
                args.AddRange(["-c:v", "libvpx-vp9", "-crf", crf, "-b:v", "0", "-row-mt", "1",
                               "-c:a", "libopus", "-b:a", Quality.AudioBitrate(o)]);
                AddVideoScale(args, o);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        new MediaTarget
        {
            Ext = "avi", Label = "AVI", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "mpeg4", Muxer = "avi", Hint = "MPEG-4 + MP3",
            Args = (source, o) =>
            {
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                args.AddRange(["-c:v", "mpeg4", "-q:v", Quality.ImageQuality(o), "-c:a", "libmp3lame", "-b:a", Quality.AudioBitrate(o)]);
                AddVideoScale(args, o);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        new MediaTarget
        {
            Ext = "wmv", Label = "WMV", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "wmv2", Muxer = "asf", Hint = "Windows Media",
            Args = (source, o) =>
            {
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                args.AddRange(["-c:v", "wmv2", "-b:v", "2M", "-c:a", "wmav2", "-b:a", Quality.AudioBitrate(o)]);
                AddVideoScale(args, o);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        new MediaTarget
        {
            Ext = "gif", Label = "GIF", Output = MediaKind.Video, Source = MediaKind.Video,
            Encoder = "gif", Muxer = "gif", Hint = "animated, no sound",
            Args = (source, o) =>
            {
                var args = new List<string>();
                FfArgs.AddInput(args, o, source);
                var width = o.ResizeWidth?.ToString() ?? "480";
                args.AddRange(["-vf", $"fps=12,scale={width}:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0"]);
                FfArgs.AddMetadata(args, o);
                return args;
            },
        },
        AudioFromVideo("mp3", "MP3", "libmp3lame", "mp3", o => ["-q:a", Quality.Mp3Quality(o)]),
        AudioFromVideo("m4a", "M4A", "aac", "mp4", o => ["-b:a", Quality.AudioBitrate(o)]),
        AudioFromVideo("wav", "WAV", "pcm_s16le", "wav", _ => []),
        AudioFromVideo("flac", "FLAC", "flac", "flac", _ => []),
    ];

    private static MediaTarget AudioFromVideo(string ext, string label, string encoder, string muxer,
        Func<ConvertOptions, string[]> codecArgs) => new()
    {
        Ext = ext,
        Label = label,
        Output = MediaKind.Audio,
        Source = MediaKind.Video,
        Encoder = encoder,
        Muxer = muxer,
        Hint = "extract audio",
        Args = (source, o) =>
        {
            var args = new List<string>();
            FfArgs.AddInput(args, o, source);
            args.Add("-vn");
            args.AddRange(["-c:a", encoder]);
            args.AddRange(codecArgs(o));
            FfArgs.AddMetadata(args, o);
            return args;
        },
    };

    private static List<MediaTarget> AudioTargets() =>
    [
        AudioTarget("mp3", "MP3", "libmp3lame", "mp3", o => ["-q:a", Quality.Mp3Quality(o)]),
        AudioTarget("m4a", "M4A", "aac", "mp4", o => ["-b:a", Quality.AudioBitrate(o)]),
        AudioTarget("wav", "WAV", "pcm_s16le", "wav", _ => []),
        AudioTarget("flac", "FLAC", "flac", "flac", _ => []),
        AudioTarget("ogg", "OGG", "libvorbis", "ogg", o => ["-q:a", o.Compress ? "4" : "6"]),
        AudioTarget("opus", "OPUS", "libopus", "opus", o => ["-b:a", Quality.AudioBitrate(o)]),
        AudioTarget("aiff", "AIFF", "pcm_s16be", "aiff", _ => []),
        AudioTarget("wma", "WMA", "wmav2", "asf", o => ["-b:a", Quality.AudioBitrate(o)]),
    ];

    private static MediaTarget AudioTarget(string ext, string label, string encoder, string muxer,
        Func<ConvertOptions, string[]> codecArgs) => new()
    {
        Ext = ext,
        Label = label,
        Output = MediaKind.Audio,
        Source = MediaKind.Audio,
        Encoder = encoder,
        Muxer = muxer,
        Args = (source, o) =>
        {
            var args = new List<string>();
            FfArgs.AddInput(args, o, source);
            args.AddRange(["-c:a", encoder]);
            args.AddRange(codecArgs(o));
            FfArgs.AddMetadata(args, o);
            return args;
        },
    };

    // Ordered to match the macOS wheel: JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP, then PDF.
    private static List<MediaTarget> ImageTargets() =>
    [
        new MediaTarget
        {
            Ext = "jpg", Label = "JPG", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "mjpeg", Muxer = "image2", Hint = "lossy, small",
            Args = (source, o) => StillImage(source, o, ["-c:v", "mjpeg", "-q:v", Quality.ImageQuality(o), "-pix_fmt", "yuvj420p"], evenDimensions: false),
        },
        new MediaTarget
        {
            Ext = "png", Label = "PNG", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "png", Muxer = "image2", Hint = "lossless",
            Args = (source, o) => StillImage(source, o, ["-c:v", "png"], evenDimensions: false),
        },
        new MediaTarget
        {
            Ext = "webp", Label = "WebP", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "libwebp", Muxer = "webp", Hint = "modern, small",
            Args = (source, o) => StillImage(source, o, ["-c:v", "libwebp", "-q:v", Quality.WebpQuality(o)], evenDimensions: false),
        },
        new MediaTarget
        {
            Ext = "heic", Label = "HEIC", Output = MediaKind.Image, Source = MediaKind.Image,
            NeedsWic = true, Hint = "Apple / iPhone format",
        },
        new MediaTarget
        {
            Ext = "tiff", Label = "TIFF", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "tiff", Muxer = "image2", Hint = "print / archiving",
            Args = (source, o) => StillImage(source, o, ["-c:v", "tiff"], evenDimensions: false),
        },
        new MediaTarget
        {
            Ext = "avif", Label = "AVIF", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "libaom-av1", Muxer = "avif", Hint = "AV1 image",
            Args = (source, o) => StillImage(source, o, ["-c:v", "libaom-av1", "-crf", o.Compress ? "34" : "28", "-b:v", "0", "-still-picture", "1"], evenDimensions: false),
        },
        new MediaTarget
        {
            Ext = "bmp", Label = "BMP", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "bmp", Muxer = "image2", Hint = "uncompressed",
            Args = (source, o) => StillImage(source, o, ["-c:v", "bmp"], evenDimensions: false),
        },
        new MediaTarget
        {
            Ext = "pdf", Label = "PDF", Output = MediaKind.Pdf, Source = MediaKind.Image,
            NeedsPdfWrite = true, Hint = "image → PDF",
        },
        new MediaTarget
        {
            Ext = "gif", Label = "GIF", Output = MediaKind.Image, Source = MediaKind.Image,
            Encoder = "gif", Muxer = "gif", Hint = "single frame",
            Args = (source, o) => StillImage(source, o, ["-c:v", "gif"], evenDimensions: false),
        },
    ];

    private static List<MediaTarget> PdfTargets() =>
    [
        new MediaTarget
        {
            Ext = "jpg", Label = "JPG", Output = MediaKind.Image, Source = MediaKind.Pdf,
            NeedsPdfRender = true, Hint = "300 DPI page images",
        },
        new MediaTarget
        {
            Ext = "png", Label = "PNG", Output = MediaKind.Image, Source = MediaKind.Pdf,
            NeedsPdfRender = true, Hint = "300 DPI page images",
        },
        new MediaTarget
        {
            Ext = "txt", Label = "TXT", Output = MediaKind.Document, Source = MediaKind.Pdf,
            NeedsPdfText = true, Hint = "extract text",
        },
    ];

    private static List<MediaTarget> SubtitleTargets() =>
    [
        TextTarget("txt", "TXT", MediaKind.Document, "plain transcript"),
        TextTarget("srt", "SRT", MediaKind.Subtitle, "SubRip"),
        TextTarget("vtt", "VTT", MediaKind.Subtitle, "WebVTT"),
    ];

    private static List<MediaTarget> DelimitedTargets() =>
    [
        TextTarget("csv", "CSV", MediaKind.Delimited, "comma separated"),
        TextTarget("tsv", "TSV", MediaKind.Delimited, "tab separated"),
    ];

    private static MediaTarget TextTarget(string ext, string label, MediaKind output, string hint) => new()
    {
        Ext = ext,
        Label = label,
        Output = output,
        NeedsText = true,
        Hint = hint,
    };

    private static List<MediaTarget> DocumentTargets() =>
    [
        OfficeTarget("pdf", "PDF", MediaKind.Pdf),
        OfficeTarget("txt", "TXT", MediaKind.Document),
        OfficeTarget("docx", "DOCX", MediaKind.Document),
        OfficeTarget("odt", "ODT", MediaKind.Document),
        OfficeTarget("epub", "EPUB", MediaKind.Document),
        OfficeTarget("html", "HTML", MediaKind.Document),
        OfficeTarget("rtf", "RTF", MediaKind.Document),
    ];

    private static MediaTarget OfficeTarget(string ext, string label, MediaKind output) => new()
    {
        Ext = ext,
        Label = label,
        Output = output,
        Source = MediaKind.Document,
        NeedsOffice = true,
        Hint = "via LibreOffice",
    };

    // ---------------------------------------------------------------- helpers

    private static List<string> StillImage(string source, ConvertOptions options, string[] codecArgs, bool evenDimensions)
    {
        var args = new List<string>();
        args.Add("-i");
        args.Add(source);
        args.AddRange(codecArgs);
        args.Add("-frames:v");
        args.Add("1");

        if (options.ResizeWidth is { } width)
        {
            args.Add("-vf");
            args.Add(FfArgs.ScaleFilter(width, evenDimensions));
        }

        FfArgs.AddMetadata(args, options);
        return args;
    }

    private static void AddVideoScale(List<string> args, ConvertOptions options)
    {
        if (options.ResizeWidth is { } width)
        {
            args.Add("-vf");
            args.Add(FfArgs.ScaleFilter(width, evenDimensions: true));
        }
    }

    private static void ApplyAvailability(MediaTarget target)
    {
        var caps = Toolchain.Caps;

        if (target.NeedsOffice)
        {
            if (!Toolchain.OfficeReady)
            {
                target.Available = false;
                target.UnavailableReason = "Needs LibreOffice";
            }
            return;
        }

        if (target.NeedsWic)
        {
            if (!WicImage.CanEncode(target.Ext))
            {
                target.Available = false;
                target.UnavailableReason = "Needs the Windows HEIF codec";
            }
            return;
        }

        if (target.NeedsPdfWrite || target.NeedsPdfRender || target.NeedsPdfText)
            return;

        // Pure-text conversions are built in, so they never depend on an external tool.
        if (target.NeedsText)
            return;

        if (!Toolchain.FfmpegReady)
        {
            target.Available = false;
            target.UnavailableReason = "ffmpeg not found";
            return;
        }

        if (target.Encoder is { Length: > 0 } encoder && !caps.HasEncoder(encoder))
        {
            target.Available = false;
            target.UnavailableReason = $"ffmpeg lacks the {encoder} encoder";
            return;
        }

        if (target.Muxer is { Length: > 0 } muxer && !caps.HasMuxer(muxer))
        {
            target.Available = false;
            target.UnavailableReason = $"ffmpeg lacks the {muxer} muxer";
        }
    }
}
