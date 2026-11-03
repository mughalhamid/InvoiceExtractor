# InvoiceExtractor (v2)

Console app: invoice PDF/image → local vision model (Ollama) → structured JSON + review flags.

Guiding rule: **the model reads, C# decides.** The model copies what is on the page;
column meanings, arithmetic and business rules are plain, testable C#.

## Pipeline

```
Program.cs                    orchestrates the steps below
 1. Pdf/PdfRenderer           PDF → one PNG per page (password for locked PDFs)
 2. Extraction/InvoiceSchema  C# records → JSON schema (the output contract), with list limits
 3. Ai/IVisionModelClient     provider-agnostic interface
    Ai/OllamaVisionClient     POST localhost:11434/api/chat with image + prompt + schema (streamed)
 4. Models/InvoiceExtraction  what the model returns: the item table copied verbatim
 5. Mapping/ColumnMapper      printed column headers → our fields, using column-mappings.json
 6. Rules/ReviewRules         signature rule, unmapped columns, totals reconciliation
    Models/InvoiceResult      the final output
```

`Extraction/Prompts.cs` holds the instructions. Change one thing at a time and re-run.

## What changed in v2

| Problem in v1 | Fix |
| --- | --- |
| Model looped, repeating a handwritten note 70+ times | Every list has a `maxItems` limit in the schema, so the grammar cannot exceed it; prompt says each note once |
| "Unit" and "Consum" columns guessed as VAT fields | The table is copied verbatim (headers + cells); `column-mappings.json` maps headers to fields; unknown headers are kept and flagged, never guessed |
| Expiry-date corrections had nowhere to go | All printed columns are kept, so a correction can name any column |
| Customer address returned as "Account No" | Prompt defines an address as street/town lines |
| No arithmetic check | C# sums the line totals, compares them with the subtotal, and computes the total after strikeouts and corrections |

## Column mappings

`column-mappings.json` lists, per field, the header spellings that mean it. Headers are
compared lower-case with spaces, dots and colons removed (`"Unit Price:"` → `unitprice`;
`"+ VAT"` → `+vat`). Add a supplier's header only once its meaning is confirmed.
The file is copied next to the executable, so you can edit it without recompiling.

## Run

```powershell
dotnet --list-sdks            # targets net10.0; change <TargetFramework> in the .csproj if needed
ollama list                   # qwen3-vl:4b should be there; Ollama must be running
dotnet build
dotnet run -- samples\Scan_For_Loui_1.pdf
dotnet run -- samples\scan_invoice_with_price_change_2.pdf
```

Options: `--model`, `--dpi` (default 150), `--password`, `--ctx` (default 12288),
`--max-tokens` (default 4096), `--timeout` minutes per page (default 20), `--think`, `--quiet`,
`--keep-loaded`, `--out`. Press Ctrl+C to stop cleanly; the model is unloaded at the end.

## Output (per run, in `output\<file>_<model>_<timestamp>\`)

- `page-1.png`          what the model saw
- `schema.json`         the contract sent to the model
- `page-1.raw.json`     the model's raw answer (verbatim table)
- `page-1.partial.txt`  what was generated before a timeout or Ctrl+C
- `page-1.json`         mapped invoice, column mapping report, reconciliation and review flags

## Expected results on the samples (ground truth)

| | Scan_For_Loui_1 | scan_invoice_with_price_change_2 |
| --- | --- | --- |
| Full strikeouts | .N13 Linguine, Valor 400342 almonds | .N5 Spaghetti, .N50 Rigatoni, Valor 400342 almonds |
| Partial strikeouts | Panfette total 6.50 → 7; .N54 Riccioli expiry 06/07/29 → 09/10/30; .N88 +VAT 1.80 → 2 | Ballara total 8.00 → 10.00 |
| Signature by each change | Yes (initials) | Spaghetti and Rigatoni: none (should be flagged) |
| Stamp | Il-Mosta, PO 1321591, received 28/8/26, signed | same |
| Handwritten notes | €71.31 by the totals | €71.31 by the totals; "This is a handwritten note" at the bottom |
| Subtotal | 91.81 | 91.81 |

## Known limits / next steps

- Pen marks are still the weak spot for a 4B model. Next: a second, focused pass that only
  looks for pen marks, and comparing `qwen3-vl:8b` / `30b` / a hosted model.
- The default `qwen3-vl:4b` tag may think before answering (slow, uses the token budget).
  If an instruct-only tag is available in Ollama, try it with `--model`.
- No bounding boxes yet, so "signature nearby" is the model's judgement, not a measured distance.
