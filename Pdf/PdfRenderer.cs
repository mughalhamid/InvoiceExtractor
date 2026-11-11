using PDFtoImage;
using SkiaSharp;

namespace InvoiceExtractor.Pdf;

/// <summary>
/// Step 1 of the pipeline: turn the input file into page images the model can "see".
/// Scanned invoices have no text layer, so an image is the only reliable input.
/// </summary>
public static class PdfRenderer
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>Returns the path of one PNG per page.</summary>
    public static List<string> RenderToImages(string inputPath, string outputDir, int dpi, string? password)
    {
        Directory.CreateDirectory(outputDir);
        var ext = Path.GetExtension(inputPath).ToLowerInvariant();

        // Images need no rendering: pass them straight through.
        if (ImageExtensions.Contains(ext))
            return [inputPath];

        if (ext != ".pdf")
            throw new NotSupportedException($"Unsupported file type '{ext}'. Use PDF, PNG or JPG.");

        var pdfBytes = File.ReadAllBytes(inputPath);

#pragma warning disable CA1416 // PDFtoImage supports Windows, Linux and macOS
        // A password-protected ("locked") PDF needs its password to open.
        var pageCount = Conversion.GetPageCount(pdfBytes, password: password);
        var pages = new List<string>(pageCount);

        for (var i = 0; i < pageCount; i++)
        {
            var pngPath = Path.Combine(outputDir, $"page-{i + 1}.png");
            Conversion.SavePng(pngPath, pdfBytes, page: new Index(i), password: password,
                options: new RenderOptions(Dpi: dpi));
            pages.Add(pngPath);
        }
#pragma warning restore CA1416

        return pages;
    }

    /// <summary>
    /// A sharper copy of one page for the pen-mark pass: re-rendered from the PDF at
    /// <paramref name="dpi"/>, or, for an image input, the image enlarged
    /// <paramref name="imageScale"/> times (no new detail, but bigger strokes for the model).
    /// </summary>
    public static SKBitmap RenderHighRes(string inputPath, string pageImagePath, int pageIndex, int dpi,
        string? password, int imageScale = 2)
    {
        if (Path.GetExtension(inputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable CA1416
            return Conversion.ToImage(File.ReadAllBytes(inputPath), new Index(pageIndex), password,
                new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416
        }

        using var original = SKBitmap.Decode(pageImagePath)
                             ?? throw new InvalidOperationException($"Could not read image '{pageImagePath}'.");
        return original.Resize(new SKImageInfo(original.Width * imageScale, original.Height * imageScale),
            SKFilterQuality.High);
    }
}
