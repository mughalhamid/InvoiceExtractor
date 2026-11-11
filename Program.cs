using System.Text;
using System.Text.Json;
using InvoiceExtractor.Ai;
using InvoiceExtractor.Extraction;
using InvoiceExtractor.Mapping;
using InvoiceExtractor.Models;
using InvoiceExtractor.Pdf;
using InvoiceExtractor.Rules;
using SkiaSharp;

// ---------------------------------------------------------------------------
// Usage:
//   dotnet run -- <invoice.pdf|png|jpg> [options]
//
//   --model qwen3-vl:4b   which Ollama model to use
//   --dpi 150             render resolution (higher = sharper but slower)
//   --password secret     for password-protected PDFs
//   --ctx 12288           context window (image + prompt + answer)
//   --max-tokens 4096     hard cap on the answer length
//   --timeout 40          minutes allowed per page (full-page pass; CPU-only runs take 20+ min)
//   --think               let the model reason before answering (slower)
//   --quiet               show a token counter instead of the live text
//   --keep-loaded         don't unload the model from memory at the end
//   --out output          output folder
//   --no-pen-pass         skip the focused second pass over coloured pen ink
//   --ink-only            only render pages and save the ink strips (no model): for tuning
//
// Press Ctrl+C at any time to stop cleanly.
// Exit codes: 0 = every page extracted, 1 = stopped (bad input, Ollama error, Ctrl+C),
//             2 = finished but at least one page has no result (timeout, cut off, invalid JSON).
// ---------------------------------------------------------------------------

var options = CliOptions.Parse(args);
if (options is null) return 1;

// Ctrl+C cancels the current request instead of killing the process,
// so the cleanup in "finally" (unloading the model) still runs.
using var userCancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    userCancel.Cancel();
};

var runDir = Path.Combine(options.OutputDir,
    $"{Path.GetFileNameWithoutExtension(options.InputPath)}_{options.Model.Replace(':', '-')}_{DateTime.Now:yyyyMMdd-HHmmss}");
Directory.CreateDirectory(runDir);

// 1. Ingest: PDF -> page images
Console.WriteLine($"Rendering '{options.InputPath}' at {options.Dpi} DPI...");
List<string> pages;
try
{
    pages = PdfRenderer.RenderToImages(options.InputPath, runDir, options.Dpi, options.Password);
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    // e.g. wrong or missing password for a locked PDF, unsupported file type, damaged file
    Console.Error.WriteLine($"Could not open '{options.InputPath}': {ex.Message}");
    if (options.Password is null)
        Console.Error.WriteLine("  If the PDF is password-protected, pass --password.");
    return 1;
}
Console.WriteLine($"  {pages.Count} page(s).");

// The pen-mark pass crops from a sharper render of the same page (2x the page DPI, at least 300)
var penDpi = Math.Max(300, options.Dpi * 2);
SKBitmap RenderForPen(int pageIndex) =>
    PdfRenderer.RenderHighRes(options.InputPath, pages[pageIndex], pageIndex, penDpi, options.Password);

if (options.InkOnly)
{
    // No model: shows where the pen-mark pass would look, for tuning Vision/InkDetector.cs
    for (var i = 0; i < pages.Count; i++)
    {
        using var hiRes = RenderForPen(i);
        var (bands, images) = PenMarkPass.Prepare(pages[i], hiRes, runDir, i + 1);
        Console.WriteLine($"Page {i + 1}: {bands.Count} ink band(s) -> {images.Count} image(s) at {penDpi} DPI");
        foreach (var b in bands)
            Console.WriteLine($"  y {b.Top}-{b.Bottom}  x {b.Left}-{b.Right}  {b.Pixels} px  ({b.Source})");
        foreach (var (file, image) in images)
            Console.WriteLine($"  {Path.GetFileName(file)}: {image.Regions.Count} strip(s), {image.Png.Length / 1024} KB");
    }
    Console.WriteLine($"\nAll output in: {Path.GetFullPath(runDir)}");
    return 0;
}

// Column header -> field mapping, applied in C# after the model has read the table verbatim
var columnMapper = ColumnMapper.Load(Path.Combine(AppContext.BaseDirectory, "column-mappings.json"));
// Known suppliers by VAT number: C# decides which address block is the supplier
var partyResolver = PartyResolver.Load(Path.Combine(AppContext.BaseDirectory, "suppliers.json"));

// 2. Build the output contract (JSON schema) from the C# records
var schema = InvoiceSchema.Build();
await File.WriteAllTextAsync(Path.Combine(runDir, "schema.json"), schema.ToJsonString(InvoiceSchema.JsonOptions));

// The "format" field forces the JSON shape, but the model never sees it as text.
// Putting the schema in the prompt too tells the model what each key means.
var prompt = Prompts.ExtractInvoice + "\n\nReturn JSON that matches this schema:\n" + schema.ToJsonString();

// 3. Talk to the model through the interface, not a concrete provider
using var http = new HttpClient
{
    BaseAddress = new Uri("http://localhost:11434"),
    Timeout = Timeout.InfiniteTimeSpan   // we control timeouts per page with a CancellationToken
};
IVisionModelClient model = new OllamaVisionClient(http, new OllamaSettings(
    options.Model, options.ContextSize, options.MaxTokens, options.Think, KeepAlive: "5m"));

// Pen-mark pass: same model and context size (so Ollama does not reload it), short answers
// (only the changes, typically 0-6 of them; at most InvoiceSchema.MaxPenMarks, ~80 tokens each).
// 2000 leaves room above that; 1200 was hit on the first run.
var penPass = new PenMarkPass(
    new OllamaVisionClient(http, new OllamaSettings(
        options.Model, options.ContextSize, MaxOutputTokens: 2000, options.Think, KeepAlive: "5m")),
    // One stacked image takes roughly 5-10 min on a CPU; half the page timeout (20 min by default).
    TimeSpan.FromMinutes(Math.Max(10, options.TimeoutMinutes / 2.0)));

var failedPages = 0;
try
{
    for (var i = 0; i < pages.Count; i++)
    {
        Console.WriteLine($"\nPage {i + 1}: sending to {options.Model} " +
                          $"(timeout {options.TimeoutMinutes} min, Ctrl+C to stop)...");
        Console.WriteLine("  Loading the model and reading the image can take a minute before text appears.\n");

        var image = await File.ReadAllBytesAsync(pages[i]);
        var partial = new StringBuilder();
        var tokenCount = 0;
        var wasThinking = false;

        void OnChunk(StreamChunk chunk)
        {
            tokenCount++;
            if (!chunk.IsThinking) partial.Append(chunk.Text);

            if (options.Quiet)
            {
                if (tokenCount % 50 == 0) Console.Write($"\r  {tokenCount} tokens generated...");
                return;
            }

            if (chunk.IsThinking != wasThinking)
            {
                Console.ResetColor();
                Console.WriteLine(chunk.IsThinking ? "\n[thinking]" : "\n[answer]");
                wasThinking = chunk.IsThinking;
            }
            if (chunk.IsThinking) Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(chunk.Text);
            Console.ResetColor();
        }

        using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(userCancel.Token);
        pageCts.CancelAfter(TimeSpan.FromMinutes(options.TimeoutMinutes));

        ModelResponse response;
        try
        {
            response = await model.ExtractAsync(prompt, image, schema, OnChunk, pageCts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.ResetColor();
            var partialPath = Path.Combine(runDir, $"page-{i + 1}.partial.txt");
            await File.WriteAllTextAsync(partialPath, partial.ToString());

            Console.Error.WriteLine(userCancel.IsCancellationRequested
                ? "\n\nStopped by user (Ctrl+C)."
                : $"\n\nPage {i + 1} timed out after {options.TimeoutMinutes} min ({tokenCount} tokens generated).");
            Console.Error.WriteLine($"  Partial output saved to {partialPath}");

            if (userCancel.IsCancellationRequested) return 1;
            failedPages++;
            continue;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Could not reach Ollama at http://localhost:11434. Is it running? ({ex.Message})");
            return 1;
        }
        catch (OllamaException ex)
        {
            Console.Error.WriteLine($"Ollama error: {ex.Message}");
            return 1;
        }

        Console.ResetColor();
        var rawPath = Path.Combine(runDir, $"page-{i + 1}.raw.json");
        await File.WriteAllTextAsync(rawPath, response.Json);

        Console.WriteLine($"\n\n  Done in {response.Duration.TotalSeconds:F0}s | " +
                          $"prompt tokens: {response.PromptTokens} | output tokens: {response.OutputTokens} | " +
                          $"speed: {response.TokensPerSecond:F1} tokens/s");

        if (response.WasTruncated)
        {
            Console.Error.WriteLine($"  The answer hit the {options.MaxTokens}-token cap and was cut off. " +
                                    "If the text above repeats itself, the model got stuck in a loop. " +
                                    "Otherwise raise --max-tokens.");
            failedPages++;
            continue;
        }

        // 4. Validate the shape: if this fails, the model broke the contract
        InvoiceExtraction? extraction;
        try
        {
            extraction = JsonSerializer.Deserialize<InvoiceExtraction>(response.Json, InvoiceSchema.JsonOptions);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"  Model output is not valid for the schema: {ex.Message}");
            Console.Error.WriteLine($"  Raw output saved to {rawPath}");
            failedPages++;
            continue;
        }
        if (extraction is null)
        {
            Console.Error.WriteLine("  Model returned null instead of an invoice.");
            failedPages++;
            continue;
        }

        // 5a. Rows: join a wrapped description line that the model returned as an extra row.
        //     Done before everything else, so pass 2, the merger and the mapper see the same rows.
        var (joinedRows, rowNotes) = WrappedRows.Merge(ColumnMapper.ItemRows(extraction));
        extraction = extraction with { ItemTable = new PrintedTable(ColumnMapper.ItemHeaders(extraction), joinedRows) };

        // 5b. Parties: C# decides which copied address block is the supplier and which the customer
        var parties = partyResolver.Resolve(extraction.LogoText, extraction.AddressBlocks);

        // 6. Pen-mark pass: C# finds the ink, the model reads only those strips (sharper), C# places the marks
        var penNotes = new List<string>(rowNotes);
        Dictionary<int, RowMarks>? penMarks = null;
        var penChecked = false;
        var headers = ColumnMapper.ItemHeaders(extraction);
        var rows = ColumnMapper.ItemRows(extraction);
        if (options.PenPass && rows.Count > 0)
        {
            Console.WriteLine($"\n  Pen-mark pass (ink strips at {penDpi} DPI):");
            try
            {
                using var hiRes = RenderForPen(i);
                var pen = await penPass.RunAsync(pages[i], hiRes, headers,
                    rows.Select(r => columnMapper.RowLabel(headers, r)).ToList(), runDir, i + 1, userCancel.Token);
                (penMarks, var mergeNotes) = PenMarkMerger.Merge(headers, rows, pen.Marks);
                penNotes.AddRange(pen.Notes);
                penNotes.AddRange(mergeNotes);
                penChecked = pen.Images.All(img => img.Error is null);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("\n\nStopped by user (Ctrl+C).");
                return 1;
            }
            catch (Exception ex) when (ex is HttpRequestException or OllamaException or InvalidOperationException)
            {
                penNotes.Add($"Pen-mark pass failed ({ex.Message}).");
            }
        }

        // 7. Map printed columns to our fields, then apply business rules (both plain C#)
        var invoice = ReviewRules.Apply(
            columnMapper.Map(extraction, parties, penMarks, penChecked) with { ReviewFlags = penNotes });

        var result = new { Source = options.InputPath, Page = i + 1, response.Model, Invoice = invoice };
        var resultPath = Path.Combine(runDir, $"page-{i + 1}.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, InvoiceSchema.JsonOptions));

        PrintSummary(invoice);
        Console.WriteLine($"  Saved: {resultPath}");
    }
}
finally
{
    Console.ResetColor();
    if (!options.KeepLoaded)
    {
        Console.WriteLine($"\nUnloading {options.Model} from memory...");
        await model.UnloadAsync();
    }
}

Console.WriteLine($"\nAll output in: {Path.GetFullPath(runDir)}");
if (failedPages > 0)
{
    Console.Error.WriteLine($"{failedPages} of {pages.Count} page(s) have no result.");
    return 2;
}
return 0;

static void PrintSummary(InvoiceResult inv)
{
    Console.WriteLine($"  Supplier : {inv.Supplier?.Name} | VAT {inv.Supplier?.VatNumber} | {inv.Supplier?.Address}");
    Console.WriteLine($"  Customer : {inv.Customer?.Name} | account {inv.Customer?.AccountNumber} | VAT {inv.Customer?.VatNumber} | {inv.Customer?.Address}");
    Console.WriteLine($"  Invoice  : {inv.InvoiceNumber} | {inv.InvoiceDate} | Total {inv.Totals?.Total} {inv.Currency}");
    Console.WriteLine($"  Lines    : {inv.LineItems.Count} " +
                      $"(struck out: {inv.LineItems.Count(l => l.IsFullyStruckOut)}, " +
                      $"corrections: {inv.LineItems.Sum(l => l.Corrections.Count)})");
    Console.WriteLine($"  Columns  : mapped {string.Join(", ", inv.ColumnMapping.Mapped.Select(m => $"{m.Key}->{m.Value}"))}");
    if (inv.ColumnMapping.Unmapped.Count > 0)
        Console.WriteLine($"             unmapped {string.Join(", ", inv.ColumnMapping.Unmapped)}");
    Console.WriteLine($"  Stamps   : {inv.Stamps.Count} | Handwritten notes: {inv.HandwrittenNotes.Count} | " +
                      $"Signature boxes: {inv.Signatures.Count} ({inv.Signatures.Count(s => s.IsSigned)} signed)");
    Console.WriteLine($"  Footer   : VAT table rows: {inv.VatSummary.Rows.Count} | " +
                      $"other charges: {string.Join(", ", inv.OtherCharges.Select(c => $"{c.Label} {c.Amount}"))}");
    Console.WriteLine($"  Parties  : {string.Join(" ", inv.Parties.Decisions)}");
    Console.WriteLine($"  Pen marks: {(inv.PenMarksChecked ? "checked" : "NOT checked")}");

    if (inv.Reconciliation is { } r)
        Console.WriteLine($"  Maths    : lines sum {r.SumOfPrintedLines:0.00} vs subtotal {r.PrintedSubtotal:0.00} " +
                          $"({(r.PrintedLinesMatchSubtotal ? "match" : "MISMATCH")}); " +
                          $"after strikeouts and corrections {r.AdjustedTotal:0.00}");

    if (inv.ReviewFlags.Count == 0)
    {
        Console.WriteLine("  Review   : no flags");
        return;
    }
    Console.WriteLine("  Review flags:");
    foreach (var f in inv.ReviewFlags) Console.WriteLine($"    - {f}");
}

record CliOptions(
    string InputPath, string Model, int Dpi, string? Password, int ContextSize, int MaxTokens,
    int TimeoutMinutes, bool Think, bool Quiet, bool KeepLoaded, string OutputDir, bool PenPass, bool InkOnly)
{
    private static readonly string[] Flags = ["--think", "--quiet", "--keep-loaded", "--no-pen-pass", "--ink-only"];
    private static readonly string[] Valued = ["--model", "--dpi", "--password", "--ctx", "--max-tokens", "--timeout", "--out"];

    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("Usage: dotnet run -- <invoice.pdf|png|jpg> [--model qwen3-vl:4b] [--dpi 150] " +
                                    "[--password secret] [--ctx 12288] [--max-tokens 4096] [--timeout 40] " +
                                    "[--think] [--quiet] [--keep-loaded] [--out output] [--no-pen-pass] [--ink-only]");
            return null;
        }

        // Typos would otherwise be ignored silently, e.g. "--pasword" running without the password.
        for (var a = 1; a < args.Length; a++)
        {
            if (Valued.Contains(args[a])) { a++; continue; }
            if (Flags.Contains(args[a])) continue;
            Console.Error.WriteLine($"Unknown option '{args[a]}'.");
            return null;
        }
        if (!File.Exists(args[0]))
        {
            Console.Error.WriteLine($"File not found: {args[0]}");
            return null;
        }

        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        int Int(string name, int fallback) => int.TryParse(Value(name), out var v) ? v : fallback;

        return new CliOptions(
            InputPath: args[0],
            Model: Value("--model") ?? "qwen3-vl:4b",
            Dpi: Int("--dpi", 150),
            Password: Value("--password"),
            ContextSize: Int("--ctx", 12288),
            MaxTokens: Int("--max-tokens", 4096),
            TimeoutMinutes: Int("--timeout", 40),
            Think: args.Contains("--think"),
            Quiet: args.Contains("--quiet"),
            KeepLoaded: args.Contains("--keep-loaded"),
            OutputDir: Value("--out") ?? "output",
            PenPass: !args.Contains("--no-pen-pass"),
            InkOnly: args.Contains("--ink-only"));
    }
}
