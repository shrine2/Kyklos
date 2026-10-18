namespace Tangerine.Core;

/// <summary>Quality presets shared by every ffmpeg target and by the "Compress" wheel tool.</summary>
internal static class Quality
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Best = "best";

    public static string Normalize(string? quality) => quality switch
    {
        Fast => Fast,
        Best => Best,
        _ => Balanced,
    };

    /// <summary>H.264/HEVC rate factor plus encoder preset. Lower CRF = higher quality.</summary>
    public static (int Crf, string Preset) Video(ConvertOptions options)
    {
        if (!options.Compress) return (20, "medium");
        return Normalize(options.Quality) switch
        {
            Fast => (30, "veryfast"),
            Best => (22, "slow"),
            _ => (26, "medium"),
        };
    }

    public static string AudioBitrate(ConvertOptions options)
    {
        if (!options.Compress) return "192k";
        return Normalize(options.Quality) switch
        {
            Fast => "96k",
            Best => "256k",
            _ => "160k",
        };
    }

    /// <summary>libmp3lame -q:a scale (0 = best, 9 = worst).</summary>
    public static string Mp3Quality(ConvertOptions options)
    {
        if (!options.Compress) return "2";
        return Normalize(options.Quality) switch
        {
            Fast => "6",
            Best => "1",
            _ => "4",
        };
    }

    /// <summary>ffmpeg -q:v scale for image encoders (2 = high, 31 = low).</summary>
    public static string ImageQuality(ConvertOptions options)
    {
        if (!options.Compress) return "2";
        return Normalize(options.Quality) switch
        {
            Fast => "8",
            Best => "2",
            _ => "5",
        };
    }

    /// <summary>libwebp / libaom quality scale (0-100 separately handled by callers).</summary>
    public static string WebpQuality(ConvertOptions options)
    {
        if (!options.Compress) return "90";
        return Normalize(options.Quality) switch
        {
            Fast => "70",
            Best => "95",
            _ => "82",
        };
    }
}
