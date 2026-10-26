using System.Text.Json;
using InvoiceExtractor.Ai;
using InvoiceExtractor.Extraction;
using InvoiceExtractor.Models;
using InvoiceExtractor.Pdf;
using InvoiceExtractor.Rules;

// ---------------------------------------------------------------------------
// Usage:
//   dotnet run -- <invoice.pdf|png|jpg> [--model qwen3-vl:4b] [--dpi 200]
//                 [--password secret] [--ctx 16384] [--think] [--out output]
// ---------------------------------------------------------------------------

var options = CliOptions.Parse(args);
if (options is null) return 1;

var runDir = Path.Combine(options.OutputDir,
    $"{Path.GetFileNameWithoutExtension(options.InputPath)}_{options.Model.Replace(':', '-')}_{DateTime.Now:yyyyMMdd-HHmmss}");
Directory.CreateDirectory(runDir);

// 1. Ingest: PDF -> page images
Console.WriteLine($"Rendering '{options.InputPath}' at {options.Dpi} DPI...");
var pages = PdfRenderer.RenderToImages(options.InputPath, runDir, options.Dpi, options.Password);
Console.WriteLine($"  {pages.Count} page(s).");

// 2. Build the output contract (JSON schema) from the C# records
var schema = InvoiceSchema.Build();
await File.WriteAllTextAsync(Path.Combine(runDir, "schema.json"), schema.ToJsonString(InvoiceSchema.JsonOptions));

// 3. Talk to the model through the interface, not a concrete provider
using var http = new HttpClient
{
    BaseAddress = new Uri("http://localhost:11434"),
    Timeout = TimeSpan.FromMinutes(15)   // CPU/GPU split on a laptop can be slow
};
IVisionModelClient model = new OllamaVisionClient(http, options.Model, options.ContextSize, options.Think);

for (var i = 0; i < pages.Count; i++)
{
    Console.WriteLine($"\nPage {i + 1}: sending to {options.Model} (this can take a few minutes)...");
    var image = await File.ReadAllBytesAsync(pages[i]);

    ModelResponse response;
    try
    {
        response = await model.ExtractAsync(Prompts.ExtractInvoice, image, schema);
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Could not reach Ollama at http://localhost:11434. Is it running? ({ex.Message})");
        return 1;
    }

    var rawPath = Path.Combine(runDir, $"page-{i + 1}.raw.json");
    await File.WriteAllTextAsync(rawPath, response.Json);
    Console.WriteLine($"  Done in {response.Duration.TotalSeconds:F0}s " +
                      $"(prompt tokens: {response.PromptTokens}, output tokens: {response.OutputTokens}).");

    // 4. Validate the shape: if this fails, the model broke the contract
    InvoiceExtraction? invoice;
    try
    {
        invoice = JsonSerializer.Deserialize<InvoiceExtraction>(response.Json, InvoiceSchema.JsonOptions);
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"  Model output is not valid for the schema: {ex.Message}");
        Console.Error.WriteLine($"  Raw output saved to {rawPath}");
        continue;
    }
    if (invoice is null) continue;
    invoice = invoice with { LineItems = invoice.LineItems ?? new List<LineItem>() };

    // 5. Business rules in plain C#
    var flags = ReviewRules.Evaluate(invoice);

    var result = new { Source = options.InputPath, Page = i + 1, response.Model, Invoice = invoice, ReviewFlags = flags };
    var resultPath = Path.Combine(runDir, $"page-{i + 1}.json");
    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, InvoiceSchema.JsonOptions));

    PrintSummary(invoice, flags);
    Console.WriteLine($"  Saved: {resultPath}");
}

Console.WriteLine($"\nAll output in: {Path.GetFullPath(runDir)}");
return 0;

static void PrintSummary(InvoiceExtraction inv, List<string> flags)
{
    Console.WriteLine($"  Supplier : {inv.Supplier?.Name} | VAT {inv.Supplier?.VatNumber}");
    Console.WriteLine($"  Customer : {inv.Customer?.Name} | VAT {inv.Customer?.VatNumber}");
    Console.WriteLine($"  Invoice  : {inv.InvoiceNumber} | {inv.InvoiceDate} | Total {inv.Totals?.Total} {inv.Currency}");
    Console.WriteLine($"  Lines    : {inv.LineItems.Count} " +
                      $"(struck out: {inv.LineItems.Count(l => l.IsFullyStruckOut)}, " +
                      $"corrections: {inv.LineItems.Sum(l => l.Corrections?.Count ?? 0)})");
    Console.WriteLine($"  Stamps   : {inv.Stamps?.Count ?? 0} | Notes: {inv.HandwrittenNotes?.Count ?? 0} | Signatures: {inv.Signatures?.Count ?? 0}");

    if (flags.Count == 0)
    {
        Console.WriteLine("  Review   : no flags");
        return;
    }
    Console.WriteLine("  Review flags:");
    foreach (var f in flags) Console.WriteLine($"    - {f}");
}

record CliOptions(string InputPath, string Model, int Dpi, string? Password, int ContextSize, bool Think, string OutputDir)
{
    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("Usage: dotnet run -- <invoice.pdf|png|jpg> [--model qwen3-vl:4b] [--dpi 200] " +
                                    "[--password secret] [--ctx 16384] [--think] [--out output]");
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

        return new CliOptions(
            InputPath: args[0],
            Model: Value("--model") ?? "qwen3-vl:4b",
            Dpi: int.TryParse(Value("--dpi"), out var dpi) ? dpi : 200,
            Password: Value("--password"),
            ContextSize: int.TryParse(Value("--ctx"), out var ctx) ? ctx : 16384,
            Think: args.Contains("--think"),
            OutputDir: Value("--out") ?? "output");
    }
}
