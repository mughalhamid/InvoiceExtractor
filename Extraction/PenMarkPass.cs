using System.Text.Json;
using InvoiceExtractor.Ai;
using InvoiceExtractor.Models;
using InvoiceExtractor.Vision;
using SkiaSharp;

namespace InvoiceExtractor.Extraction;

/// <summary>
/// Pass 2: pen marks. A small model reading the whole page missed every pen mark, so:
///  1. C# finds where pen ink is on the page (InkDetector: coloured ink + lines through text);
///  2. those strips are cut from a SHARPER render (300 DPI) and stacked into one image;
///  3. the model gets that image plus pass 1's rows by number, and lists only the changes;
///  4. C# checks and places each change (Mapping/PenMarkMerger.cs).
/// Unlike a fixed "table band", this works wherever the table is on the page.
/// </summary>
public sealed class PenMarkPass(IVisionModelClient model, TimeSpan timeoutPerImage)
{
    public record ImageReading(string File, List<InkDetector.Region> Regions, List<PenMark> Marks, string? Error);

    public record Result(List<ImageReading> Images, List<PenMark> Marks, List<string> Notes);

    /// <summary>Finds ink bands on the page image and saves the stacked strip images. No model call.</summary>
    public static (List<InkBand> Bands, List<(string File, InkDetector.StripImage Image)> Images) Prepare(
        string pageImagePath, SKBitmap hiRes, string runDir, int page)
    {
        using var lowRes = SKBitmap.Decode(pageImagePath)
                           ?? throw new InvalidOperationException($"Could not read image '{pageImagePath}'.");
        var bands = InkDetector.FindBands(lowRes);
        var images = InkDetector.StackStrips(hiRes, bands, lowRes.Height);

        var saved = new List<(string, InkDetector.StripImage)>();
        for (var k = 0; k < images.Count; k++)
        {
            var file = Path.Combine(runDir, images.Count == 1 ? $"page-{page}.pen.png" : $"page-{page}.pen-{k + 1}.png");
            File.WriteAllBytes(file, images[k].Png);
            saved.Add((file, images[k]));
        }
        return (bands, saved);
    }

    public async Task<Result> RunAsync(
        string pageImagePath, SKBitmap hiRes, IReadOnlyList<string> headers, IReadOnlyList<string> rowDescriptions,
        string runDir, int page, CancellationToken userCancel)
    {
        var (bands, images) = Prepare(pageImagePath, hiRes, runDir, page);
        var readings = new List<ImageReading>();
        var marks = new List<PenMark>();
        var notes = new List<string>();

        if (bands.Count == 0)
            notes.Add("No pen ink found. If this invoice has pen marks that are not lines through text " +
                      "(e.g. a small black-pen scribble), check strikeouts and corrections by hand.");
        Console.WriteLine($"  {bands.Count} ink band(s) -> {images.Count} image(s)");

        // The schema is enforced through "format" only: the prompt already explains every field,
        // and leaving the schema text out saves ~500 prompt tokens (minutes on a CPU).
        var schema = InvoiceSchema.BuildPenMarks(headers, rowDescriptions.Count);
        var prompt = Prompts.PenMarks(headers, rowDescriptions);

        foreach (var (file, image) in images)
        {
            Console.Write($"  Reading {Path.GetFileName(file)} ({image.Regions.Count} strip(s)): ");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(userCancel);
            cts.CancelAfter(timeoutPerImage);
            string? error = null;
            var found = new List<PenMark>();
            // Everything the model wrote is saved (page-N.pen.raw.txt), also when it times out or is
            // cut off, so a failed reading can be diagnosed instead of being thrown away.
            var answer = new System.Text.StringBuilder();
            var rawPath = Path.ChangeExtension(file, ".raw.txt");
            try
            {
                var response = await model.ExtractAsync(prompt, image.Png, schema,
                    chunk => { if (!chunk.IsThinking) answer.Append(chunk.Text); }, cts.Token);
                if (response.WasTruncated)
                    error = $"answer hit the token cap; see {Path.GetFileName(rawPath)}";
                else
                {
                    var listed = (JsonSerializer.Deserialize<PenMarkReading>(response.Json, InvoiceSchema.JsonOptions)?.Marks ?? [])
                        .Where(m => m is not null).ToList();
                    if (listed.Count >= InvoiceSchema.MaxPenMarks)
                        notes.Add($"Pen-mark image {Path.GetFileName(file)}: the model listed the maximum of " +
                                  $"{InvoiceSchema.MaxPenMarks} changes, so there may be more; check by hand.");
                    found = listed
                        // a model that loops repeats the same change; keep each change once
                        .DistinctBy(m => (m.RowNumber, m.Kind, m.Column, m.OriginalValue, m.NewValue))
                        .ToList();
                }
                Console.Write($"{response.Duration.TotalSeconds:F0}s, {response.OutputTokens} tokens, ");
            }
            catch (OperationCanceledException) when (!userCancel.IsCancellationRequested)
            {
                error = $"timed out after {timeoutPerImage.TotalMinutes:0} min";
            }
            catch (JsonException ex)
            {
                error = $"answer did not match the schema ({ex.Message})";
            }
            finally
            {
                await File.WriteAllTextAsync(rawPath, answer.ToString(), CancellationToken.None);
            }

            if (error is not null)
                notes.Add($"Pen-mark image {Path.GetFileName(file)} could not be read ({error}); " +
                          "check strikeouts and corrections by hand.");

            Console.WriteLine(error ?? (found.Count == 0 ? "no changes" : $"{found.Count} change(s)"));
            foreach (var m in found)
                Console.WriteLine($"    row {m.RowNumber} {m.Kind} \"{m.RowText}\"" +
                                  (m.Kind == PenMarkKind.PartialStrikeout ? $" {m.Column}: '{m.OriginalValue}' -> '{m.NewValue}'" : "") +
                                  (m.HasSignatureNearby ? " [signed]" : " [no signature]"));

            readings.Add(new ImageReading(Path.GetFileName(file), image.Regions, found, error));
            marks.AddRange(found);
        }

        await File.WriteAllTextAsync(Path.Combine(runDir, $"page-{page}.pen.json"),
            JsonSerializer.Serialize(new { Bands = bands, Images = readings }, InvoiceSchema.JsonOptions),
            CancellationToken.None);

        return new Result(readings, marks, notes);
    }
}
