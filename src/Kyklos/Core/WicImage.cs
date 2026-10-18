using System.Drawing.Imaging;
using Encoder = System.Drawing.Imaging.Encoder;

namespace Kyklos.Core;

/// <summary>
/// Image conversion through the Windows Imaging Component.
/// Used for formats ffmpeg's build cannot handle on this machine — notably HEIC/HEIF,
/// which the bundled Gyan ffmpeg has no libheif for. If the user installed the
/// "HEIF Image Extensions" from the Microsoft Store, WIC provides the codec and the
/// wheel offers HEIC; otherwise the option is shown greyed out.
/// </summary>
public static class WicImage
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, ImageCodecInfo>? _encoderByExtension;

    public static bool CanEncode(string ext)
    {
        try
        {
            return FindEncoder(ext) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryConvert(string source, string destination, string ext, out string? error)
    {
        try
        {
            Convert(source, destination, ext);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void Convert(string source, string destination, string ext)
    {
        var codec = FindEncoder(ext)
                    ?? throw new InvalidOperationException(
                        $"Windows has no image encoder for '.{ext.TrimStart('.')}'. Install the 'HEIF Image Extensions' from the Microsoft Store to enable it.");

        var bytes = File.ReadAllBytes(source);
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: false);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (TrySaveWithQuality(image, destination, codec, 92)) return;
        image.Save(destination, codec, null);
    }

    private static bool TrySaveWithQuality(Image image, string destination, ImageCodecInfo codec, long quality)
    {
        try
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            image.Save(destination, codec, parameters);
            return true;
        }
        catch
        {
            // Codec does not expose a quality knob (e.g. PNG) — caller falls back to a plain save.
            return false;
        }
    }

    private static ImageCodecInfo? FindEncoder(string ext)
    {
        var wanted = "." + ext.TrimStart('.').ToLowerInvariant();
        var map = EncodersByExtension();
        return map.TryGetValue(wanted, out var codec) ? codec : null;
    }

    private static Dictionary<string, ImageCodecInfo> EncodersByExtension()
    {
        lock (Gate)
        {
            if (_encoderByExtension is not null) return _encoderByExtension;

            var map = new Dictionary<string, ImageCodecInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (string.IsNullOrWhiteSpace(codec.FilenameExtension)) continue;
                foreach (var raw in codec.FilenameExtension.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var token = raw.Trim().TrimStart('*').ToLowerInvariant();
                    if (token.StartsWith('.')) map.TryAdd(token, codec);
                }
            }

            _encoderByExtension = map;
            return map;
        }
    }
}
