# InvoiceExtractor (v1)

Console app: invoice PDF/image → local vision model (Ollama) → structured JSON + review flags.

## Pipeline

```
Program.cs                 orchestrates the steps below
 1. Pdf/PdfRenderer        PDF → one PNG per page (password for locked PDFs)
 2. Extraction/InvoiceSchema  C# records → JSON schema (the output contract)
 3. Ai/IVisionModelClient  provider-agnostic interface
    Ai/OllamaVisionClient  POST localhost:11434/api/chat with image + prompt + schema
 4. Models/InvoiceExtraction  deserialize = validate the model kept the contract
 5. Rules/ReviewRules      plain C# business rules (signature rule for now)
```

`Extraction/Prompts.cs` holds the instructions. Change one thing at a time and re-run.

## Run

```powershell
dotnet --list-sdks            # targets net10.0; change <TargetFramework> in the .csproj if needed
ollama list                   # qwen3-vl:4b should be there; Ollama must be running
cd InvoiceExtractor
dotnet build
dotnet run -- samples\Scan_For_Loui_1.pdf
dotnet run -- samples\scan_invoice_with_price_change_2.pdf
```

Options: `--model`, `--dpi` (default 200), `--password`, `--ctx` (default 16384), `--think`, `--out`.

## Output (per run, in `output\<file>_<model>_<timestamp>\`)

- `page-1.png`       what the model saw
- `schema.json`      the contract sent to the model
- `page-1.raw.json`  the model's raw answer
- `page-1.json`      parsed invoice + review flags

## Tuning tips

- Out of memory or very slow → `--dpi 150`, or close other apps.
- Text misread → try `--dpi 300` with `--ctx 32768`.
- Hard pen marks → compare `qwen3-vl:4b`, `qwen3-vl:8b` and `--think`.
