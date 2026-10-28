using System.Text;
using System.Text.Json;
using InvoiceExtractor.Ai;
using InvoiceExtractor.Extraction;
using InvoiceExtractor.Models;
using InvoiceExtractor.Pdf;
using InvoiceExtractor.Rules;

// ---------------------------------------------------------------------------
// Usage:
//   dotnet run -- <invoice.pdf|png|jpg> [options]
//
//   --model qwen3-vl:4b   which Ollama model to use
//   --dpi 150             render resolution (higher = sharper but slower)
//   --password secret     for password-protected PDFs
//   --ctx 12288           context window (image + prompt + answer)
//   --max-tokens 4096     hard cap on the answer length
//   --timeout 20          minutes allowed per page
//   --think               let the model reason before answering (slower)
//   --quiet               show a token counter instead of the live text
//   --keep-loaded         don't unload the model from memory at the end
//   --out output          output folder
//
// Press Ctrl+C at any time to stop cleanly.
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
var pages = PdfRenderer.RenderToImages(options.InputPath, runDir, options.Dpi, options.Password);
Console.WriteLine($"  {pages.Count} page(s).");

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
            continue;
        }

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

record CliOptions(
    string InputPath, string Model, int Dpi, string? Password, int ContextSize, int MaxTokens,
    int TimeoutMinutes, bool Think, bool Quiet, bool KeepLoaded, string OutputDir)
{
    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("Usage: dotnet run -- <invoice.pdf|png|jpg> [--model qwen3-vl:4b] [--dpi 150] " +
                                    "[--password secret] [--ctx 12288] [--max-tokens 4096] [--timeout 20] " +
                                    "[--think] [--quiet] [--keep-loaded] [--out output]");
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
            TimeoutMinutes: Int("--timeout", 20),
            Think: args.Contains("--think"),
            Quiet: args.Contains("--quiet"),
            KeepLoaded: args.Contains("--keep-loaded"),
            OutputDir: Value("--out") ?? "output");
    }
}
