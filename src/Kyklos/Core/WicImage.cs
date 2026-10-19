using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Kyklos.Core;

/// <summary>
/// Image conversion through the Windows Imaging Component, reached through the WinRT
/// imaging API. This is the only route to formats the bundled ffmpeg cannot handle on
/// this machine — notably HEIC/HEIF, which Gyan's build has no libheif for; the
/// "HEIF Image Extensions" from the Microsoft Store supply that codec.
///
/// This deliberately avoids GDI+ (System.Drawing). GDI+ only ever reports its own
/// built-in codecs (BMP, JPEG, GIF, TIFF, PNG) and cannot see a WIC codec at all, so
/// asking it whether HEIC can be written answers "no" even on a machine where the HEIF
/// codec is installed and working.
/// </summary>
public static class WicImage
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, bool> EncoderCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Guid> EncoderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = BitmapEncoder.JpegEncoderId,
        ["jpeg"] = BitmapEncoder.JpegEncoderId,
        ["png"] = BitmapEncoder.PngEncoderId,
        ["bmp"] = BitmapEncoder.BmpEncoderId,
        ["gif"] = BitmapEncoder.GifEncoderId,
        ["tif"] = BitmapEncoder.TiffEncoderId,
        ["tiff"] = BitmapEncoder.TiffEncoderId,
        ["heic"] = BitmapEncoder.HeifEncoderId,
        ["heif"] = BitmapEncoder.HeifEncoderId,
    };

    /// <summary>
    /// Whether Windows can actually write this container. A container id exists in the
    /// projection whether or not any codec is installed, so the check is proved end to
    /// end by encoding a single pixel; the answer is cached because the wheel asks for
    /// it while building.
    /// </summary>
    public static bool CanEncode(string ext)
    {
        var wanted = Normalise(ext);
        lock (Gate)
        {
            if (EncoderCache.TryGetValue(wanted, out var cached)) return cached;
        }

        var available = false;
        if (EncoderIds.TryGetValue(wanted, out var id))
        {
            try
            {
                available = RunBlocking(async () =>
                {
                    using var stream = new InMemoryRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(id, stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 1, 1, 96, 96, new byte[4]);
                    await encoder.FlushAsync();
                    return stream.Size > 0;
                }, TimeSpan.FromSeconds(10));
            }
            catch
            {
                available = false;
            }
        }

        lock (Gate) EncoderCache[wanted] = available;
        return available;
    }

    /// <summary>
    /// True when ffmpeg's build cannot decode this file, so it has to be decoded by
    /// Windows first (see <see cref="Convert"/>).
    /// </summary>
    public static bool NeedsDecode(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".heic" or ".heif" => !Toolchain.Caps.HasDecoder("heif", "heic"),
        ".jxl" => !Toolchain.Caps.HasDecoder("jxl", "libjxl"),
        ".avif" => !Toolchain.Caps.HasDecoder("av1"),
        _ => false,
    };

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
        var wanted = Normalise(ext);
        if (!EncoderIds.TryGetValue(wanted, out var encoderId) || !CanEncode(wanted))
            throw new InvalidOperationException(NoEncoderMessage(wanted));

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        RunBlocking(() => EncodeFileAsync(source, destination, encoderId, wanted));
    }

    private static async Task EncodeFileAsync(string source, string destination, Guid encoderId, string ext)
    {
        using var input = File.OpenRead(source).AsRandomAccessStream();

        // Let WIC choose the decoder from the file itself, so every codec the machine has
        // (HEIF, AVIF, JPEG XL, each with its own Store extension) is usable as an input.
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var pixels = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            AlphaModeFor(ext),
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var output = File.Open(destination, FileMode.Create, FileAccess.ReadWrite).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, output);
        await TrySetQualityAsync(encoder, ext);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();
    }

    /// <summary>Containers with no alpha channel reject premultiplied pixels.</summary>
    private static BitmapAlphaMode AlphaModeFor(string ext) =>
        ext is "jpg" or "jpeg" or "bmp" ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied;

    private static async Task TrySetQualityAsync(BitmapEncoder encoder, string ext)
    {
        if (ext is not ("jpg" or "jpeg" or "heic" or "heif")) return;

        try
        {
            var properties = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(0.92f, PropertyType.Single) },
            };
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        }
        catch
        {
            // Codec has no quality knob — encode with its own defaults.
        }
    }

    private static string NoEncoderMessage(string ext) =>
        ext is "heic" or "heif"
            ? "Windows has no HEIF encoder. Install 'HEIF Image Extensions' from the Microsoft Store to enable HEIC."
            : $"Windows has no image encoder for '.{ext}'.";

    private static string Normalise(string ext) => ext.Trim().TrimStart('.').ToLowerInvariant();

    /// <summary>
    /// Runs a WinRT async operation to completion. The work is pushed onto the thread
    /// pool so it never depends on a message pump being serviced, which makes blocking
    /// on it safe even when the caller is the UI thread.
    /// </summary>
    private static T RunBlocking<T>(Func<Task<T>> work, TimeSpan? timeout = null)
    {
        var task = Task.Run(work);

        if (timeout is { } limit)
        {
            try { _ = task.Wait(limit); }
            catch (AggregateException) { /* surfaced unwrapped below */ }
            if (!task.IsCompleted)
                throw new TimeoutException($"The Windows imaging operation did not finish within {limit.TotalSeconds:0} seconds.");
        }

        return task.GetAwaiter().GetResult();
    }

    private static void RunBlocking(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();
}
