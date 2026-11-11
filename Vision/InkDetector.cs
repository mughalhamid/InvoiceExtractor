using SkiaSharp;

namespace InvoiceExtractor.Vision;

/// <summary>
/// A horizontal strip of the page that probably has pen marks.
/// Source: "colour" (coloured ink), "stroke" (a long thin dark line, e.g. a black-pen strikeout), or both.
/// </summary>
public record InkBand(int Top, int Bottom, int Left, int Right, int Pixels, string Source = "colour")
{
    public int Height => Bottom - Top + 1;
}

/// <summary>
/// Finds handwritten pen marks by colour, in plain C#.
/// Invoices are printed in black/grey; pens are usually blue (sometimes red or green).
/// So "coloured pixel" is a cheap, reliable signal of WHERE someone wrote on the page.
/// The model is then only asked to read those small areas (see Extraction/PenMarkPass.cs),
/// which a small model does far better than spotting thin pen lines on a whole page.
///
/// A black pen leaves no colour, so strikeouts are also found as STROKES: a long, thin, dark
/// line crossing several characters. Printed text has gaps between letters, and printed table
/// rules run almost the full page width, so neither counts.
///
/// Limits: a black-pen correction that is only a short scribble (no long line) is not found here;
/// the full-page reading still covers it.
/// </summary>
public static class InkDetector
{
    // Left margin ignored: reviewers put tick marks there, which are not changes.
    private const double MarginFraction = 0.05;

    // Thresholds relative to page height, so they hold at any --dpi.
    private const double MaxGapFraction = 0.004;       // merge ink lines closer than this
    private const double MaxBandHeightFraction = 0.07; // taller blocks are stamps/signatures, not row edits
    private const int MinPixelsPerLine = 2;
    private const int MinBandPixels = 25;

    /// <summary>Coloured (saturated) and not paper-white: blue, red or green ink.</summary>
    public static bool IsInk(SKColor c)
    {
        int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
        int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
        return max - min > 50 && c.Red + c.Green + c.Blue < 600;
    }

    // Stroke detection (measured on the samples at 150 DPI: printed glyph runs <= ~25 px,
    // a pen line through a row 80-130 px per pixel row, because it is slanted and wavy)
    private const int MaxLetterGap = 2;                    // a pen line is continuous; printed letters have gaps
    private const double MinSegmentFraction = 0.028;       // ~35 px at 150 DPI: longer than any printed glyph
    private const double MinTextLineFraction = 0.0045;     // ~8 px: thinner "lines" are printed rules
    private const double MaxTextLineFraction = 0.04;       // ~70 px: taller blocks are logos, barcodes, stamps

    public static bool IsDark(SKColor c) => 0.30 * c.Red + 0.59 * c.Green + 0.11 * c.Blue < 120;

    /// <summary>Coloured-ink bands and dark-stroke bands, merged where they overlap.</summary>
    public static List<InkBand> FindBands(SKBitmap bmp)
    {
        var all = FindColourBands(bmp).Concat(FindStrokeBands(bmp)).OrderBy(b => b.Top).ToList();
        int maxGap = Math.Max(2, (int)(bmp.Height * MaxGapFraction));
        var merged = new List<InkBand>();
        foreach (var b in all)
        {
            if (merged.Count > 0 && b.Top <= merged[^1].Bottom + maxGap)
            {
                var m = merged[^1];
                merged[^1] = new InkBand(m.Top, Math.Max(m.Bottom, b.Bottom), Math.Min(m.Left, b.Left),
                    Math.Max(m.Right, b.Right), m.Pixels + b.Pixels,
                    m.Source == b.Source ? m.Source : "colour+stroke");
            }
            else merged.Add(b);
        }
        return merged;
    }

    /// <summary>
    /// Text lines where a long, unbroken dark line runs through the MIDDLE of the text.
    /// 1. Find printed text lines (rows of pixels with dark pixels, separated by blank rows).
    /// 2. In the middle 60% of each line, look for dark segments longer than any printed glyph.
    /// Printed rules sit between text lines (and are too thin to be a line on their own); underlines
    /// sit at the bottom edge; so neither is counted.
    /// </summary>
    public static List<InkBand> FindStrokeBands(SKBitmap bmp)
    {
        int margin = (int)(bmp.Width * MarginFraction);
        int minSegment = Math.Max(12, (int)(bmp.Width * MinSegmentFraction));
        int minLine = Math.Max(4, (int)(bmp.Height * MinTextLineFraction));
        int maxLine = (int)(bmp.Height * MaxTextLineFraction);

        // Long segments per pixel row (rule rows are excluded later by being outside text lines' middles)
        var darkCount = new int[bmp.Height];
        var segLen = new int[bmp.Height];
        var segLeft = new int[bmp.Height];
        var segRight = new int[bmp.Height];
        for (var y = 0; y < bmp.Height; y++)
        {
            segLeft[y] = int.MaxValue;
            int runStart = -1, lastDark = -100;
            void EndRun()
            {
                if (runStart < 0) return;
                var len = lastDark - runStart + 1;
                if (len < minSegment) return;
                segLen[y] += len;
                segLeft[y] = Math.Min(segLeft[y], runStart);
                segRight[y] = Math.Max(segRight[y], lastDark);
            }
            for (var x = margin; x < bmp.Width; x++)
            {
                if (!IsDark(bmp.GetPixel(x, y))) continue;
                darkCount[y]++;
                if (x - lastDark > MaxLetterGap + 1) { EndRun(); runStart = x; }
                lastDark = x;
            }
            EndRun();
        }

        // Text lines: consecutive pixel rows with some dark pixels
        var bands = new List<InkBand>();
        int y0 = 0;
        while (y0 < bmp.Height)
        {
            if (darkCount[y0] < 3) { y0++; continue; }
            var y1 = y0;
            while (y1 + 1 < bmp.Height && darkCount[y1 + 1] >= 3) y1++;

            var h = y1 - y0 + 1;
            // A printed rule touching a text line would make the line full-width dark at one edge;
            // only the middle part is examined, so that does not count.
            if (h >= minLine && h <= maxLine)
            {
                int mTop = y0 + (int)(h * 0.2), mBottom = y1 - (int)(h * 0.2);
                int best = 0, left = int.MaxValue, right = 0;
                for (var y = mTop; y <= mBottom; y++)
                {
                    // A pixel row that is mostly dark is a printed rule or a filled box, not a pen line
                    if (darkCount[y] > (bmp.Width - margin) * 0.6) continue;
                    if (segLen[y] <= best) continue;
                    best = segLen[y]; left = segLeft[y]; right = segRight[y];
                }
                if (best >= minSegment)
                    bands.Add(new InkBand(y0, y1, left, right, best, "stroke"));
            }
            y0 = y1 + 1;
        }
        return bands;
    }

    public static List<InkBand> FindColourBands(SKBitmap bmp)
    {
        int margin = (int)(bmp.Width * MarginFraction);
        int maxGap = Math.Max(2, (int)(bmp.Height * MaxGapFraction));
        int maxHeight = (int)(bmp.Height * MaxBandHeightFraction);

        var count = new int[bmp.Height];
        var minX = new int[bmp.Height];
        var maxX = new int[bmp.Height];
        for (var y = 0; y < bmp.Height; y++)
        {
            minX[y] = int.MaxValue;
            for (var x = margin; x < bmp.Width; x++)
            {
                if (!IsInk(bmp.GetPixel(x, y))) continue;
                count[y]++;
                minX[y] = Math.Min(minX[y], x);
                maxX[y] = Math.Max(maxX[y], x);
            }
        }

        var bands = new List<InkBand>();
        int? top = null;
        int lastInk = -1, pixels = 0, left = int.MaxValue, right = 0;

        void Close()
        {
            if (top is { } t && pixels >= MinBandPixels && lastInk - t + 1 <= maxHeight)
                bands.Add(new InkBand(t, lastInk, left, right, pixels));
            top = null; pixels = 0; left = int.MaxValue; right = 0;
        }

        for (var y = 0; y < bmp.Height; y++)
        {
            if (count[y] < MinPixelsPerLine) continue;
            if (top is not null && y - lastInk > maxGap) Close();
            top ??= y;
            lastInk = y;
            pixels += count[y];
            left = Math.Min(left, minX[y]);
            right = Math.Max(right, maxX[y]);
        }
        Close();
        return bands;
    }

    /// <summary>One stacked image for the pen-mark pass, and which page rows (in the high-res page) it holds.</summary>
    public record Region(int Top, int Bottom);

    public record StripImage(byte[] Png, List<Region> Regions);

    /// <summary>
    /// Cuts the full page width around each band (so the whole row and any initials at the end
    /// are visible) from the SHARP high-res page, and stacks the strips into as few images as
    /// possible, with white gaps between them. One model call per image instead of per strip.
    /// </summary>
    /// <param name="hiRes">The page rendered at a higher DPI than the one the bands were found on.</param>
    /// <param name="bandPageHeight">Height of the page image the bands were found on.</param>
    /// <param name="maxImageHeight">Split into several images above this height (keeps image tokens bounded).</param>
    public static List<StripImage> StackStrips(SKBitmap hiRes, IReadOnlyList<InkBand> bands, int bandPageHeight,
        int maxImageHeight = 1500)
    {
        double factor = hiRes.Height / (double)bandPageHeight;
        int pad = Math.Max(8, (int)(bandPageHeight * 0.012));     // a little of the rows above and below
        int gap = Math.Max(16, (int)(hiRes.Height * 0.008));

        // Regions in high-res pixels, overlapping ones merged so no row appears twice
        var regions = new List<Region>();
        foreach (var b in bands.OrderBy(b => b.Top))
        {
            int top = Math.Max(0, (int)((b.Top - pad) * factor));
            int bottom = Math.Min(hiRes.Height - 1, (int)((b.Bottom + pad) * factor));
            if (regions.Count > 0 && top <= regions[^1].Bottom)
                regions[^1] = regions[^1] with { Bottom = Math.Max(regions[^1].Bottom, bottom) };
            else
                regions.Add(new Region(top, bottom));
        }

        // Group regions into images no taller than maxImageHeight (a single taller region gets its own image)
        var groups = new List<List<Region>>();
        int height = 0;
        foreach (var r in regions)
        {
            int h = r.Bottom - r.Top + 1;
            if (groups.Count == 0 || height + gap + h > maxImageHeight)
            {
                groups.Add([]);
                height = 0;
            }
            groups[^1].Add(r);
            height += (groups[^1].Count > 1 ? gap : 0) + h;
        }

        var images = new List<StripImage>();
        foreach (var group in groups)
        {
            int total = group.Sum(r => r.Bottom - r.Top + 1) + gap * (group.Count - 1);
            using var canvasBitmap = new SKBitmap(hiRes.Width, total);
            using (var canvas = new SKCanvas(canvasBitmap))
            {
                canvas.Clear(SKColors.White);
                int y = 0;
                foreach (var (top, bottom) in group)
                {
                    var src = new SKRectI(0, top, hiRes.Width, bottom + 1);
                    var dst = new SKRect(0, y, hiRes.Width, y + src.Height);
                    canvas.DrawBitmap(hiRes, src, dst);
                    y += src.Height + gap;
                }
            }
            using var image = SKImage.FromBitmap(canvasBitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            images.Add(new StripImage(png.ToArray(), group));
        }
        return images;
    }
}
