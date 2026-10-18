using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Encoder = System.Drawing.Imaging.Encoder;

namespace Tangerine.Core;

/// <summary>
/// PDF support with no external binaries:
///  - images → PDF uses a small built-in PDF writer (JPEG/DCTDecode streams)
///  - PDF → images uses the Windows renderer behind Edge (Windows.Data.Pdf)
///  - PDF → text uses PdfPig
/// </summary>
public static class PdfTools
{
    public static int DefaultDpi { get; set; } = 300;

    private sealed record PageImage(byte[] Jpeg, int Width, int Height);

    // ---------------------------------------------------------------- images → PDF

    public static IReadOnlyList<string> ImagesToPdf(IReadOnlyList<string> sources, string outputPath)
    {
        var pages = new List<PageImage>(sources.Count);
        foreach (var source in sources)
        {
            if (FormatCatalog.DetectKind(source) != MediaKind.Image)
                throw new InvalidOperationException($"'{Path.GetFileName(source)}' is not an image.");
            pages.Add(EncodeJpeg(source));
        }

        if (pages.Count == 0) throw new InvalidOperationException("Nothing to convert.");
        WritePdf(pages, outputPath);
        return [outputPath];
    }

    private static PageImage EncodeJpeg(string path)
    {
        using var memory = new MemoryStream(File.ReadAllBytes(path));
        using var source = Image.FromStream(memory, useEmbeddedColorManagement: true, validateImageData: false);

        // Flatten onto white: PDF DCTDecode streams are DeviceRGB, so alpha must be resolved.
        using var flattened = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(flattened))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        }

        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
                    ?? throw new InvalidOperationException("No JPEG encoder is available on this system.");

        using var output = new MemoryStream();
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            flattened.Save(output, codec, parameters);
        }

        return new PageImage(output.ToArray(), flattened.Width, flattened.Height);
    }

    private static void WritePdf(IReadOnlyList<PageImage> pages, string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var pageCount = pages.Count;
        var maxObjectId = 2 + pageCount * 3;
        var offsets = new long[maxObjectId + 1];

        void Ascii(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        Ascii("%PDF-1.4\n");
        stream.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // binary marker comment

        offsets[1] = stream.Position;
        Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 3} 0 R"));
        offsets[2] = stream.Position;
        Ascii($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var pageId = 3 + i * 3;
            var contentId = pageId + 1;
            var imageId = pageId + 2;
            var page = pages[i];

            // 96 px/inch source → 72 pt/inch PDF units
            var widthPt = (page.Width * 72.0 / 96.0).ToString("0.###", CultureInfo.InvariantCulture);
            var heightPt = (page.Height * 72.0 / 96.0).ToString("0.###", CultureInfo.InvariantCulture);
            var content = $"q {widthPt} 0 0 {heightPt} 0 0 cm /Im0 Do Q";

            offsets[pageId] = stream.Position;
            Ascii($"{pageId} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPt} {heightPt}] " +
                  $"/Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");

            offsets[contentId] = stream.Position;
            Ascii($"{contentId} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

            offsets[imageId] = stream.Position;
            Ascii($"{imageId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} " +
                  $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            stream.Write(page.Jpeg, 0, page.Jpeg.Length);
            Ascii("\nendstream\nendobj\n");
        }

        var xrefPosition = stream.Position;
        Ascii("xref\n");
        Ascii($"0 {maxObjectId + 1}\n");
        Ascii("0000000000 65535 f \n");
        for (var id = 1; id <= maxObjectId; id++)
            Ascii($"{offsets[id]:D10} 00000 n \n");

        Ascii("trailer\n");
        Ascii($"<< /Size {maxObjectId + 1} /Root 1 0 R >>\n");
        Ascii("startxref\n");
        Ascii($"{xrefPosition}\n");
        Ascii("%%EOF\n");
    }

    // ---------------------------------------------------------------- PDF → images

    public static async Task<IReadOnlyList<string>> RenderPagesAsync(
        string pdfPath, string baseOutputPath, string ext, CancellationToken ct)
    {
        var storageFile = await StorageFile.GetFileFromPathAsync(pdfPath);
        var document = await PdfDocument.LoadFromFileAsync(storageFile);

        var count = document.PageCount;
        var written = new List<string>((int)count);
        var scale = DefaultDpi / 96.0;

        for (uint index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var page = document.GetPage(index);
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
            };

            using var buffer = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(buffer, options);
            var bytes = await ReadAllBytesAsync(buffer);

            if (!string.Equals(ext, "png", StringComparison.OrdinalIgnoreCase))
                bytes = Transcode(bytes, ext);

            var destination = count == 1
                ? baseOutputPath
                : Paths.WithSuffix(baseOutputPath, $"({index + 1})");
            destination = Path.ChangeExtension(destination, ext);

            var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await File.WriteAllBytesAsync(destination, bytes, ct);
            written.Add(destination);
        }

        return written;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static byte[] Transcode(byte[] pngBytes, string ext)
    {
        var wanted = "." + ext.TrimStart('.').ToLowerInvariant();
        var codec = ImageCodecInfo.GetImageEncoders()
                        .FirstOrDefault(c => c.FilenameExtension?.ToLowerInvariant().Contains(wanted) == true)
                    ?? throw new InvalidOperationException($"No image encoder for '.{ext}'.");

        using var input = new MemoryStream(pngBytes);
        using var image = Image.FromStream(input, useEmbeddedColorManagement: true, validateImageData: false);
        using var output = new MemoryStream();
        using (var parameters = new EncoderParameters(1))
        {
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            image.Save(output, codec, parameters);
        }
        return output.ToArray();
    }

    // ---------------------------------------------------------------- PDF → text

    public static int ExtractText(string pdfPath, string outputPath)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        var builder = new StringBuilder();
        var pages = 0;

        foreach (var page in document.GetPages())
        {
            pages++;
            builder.AppendLine(page.Text);
            builder.AppendLine();
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return pages;
    }
}
