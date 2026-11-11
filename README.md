# InvoiceExtractor (v3: two passes)

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
 4. PASS 1 (whole page)       Models/InvoiceExtraction: printed content only, copied verbatim:
                              logo text, address blocks (label + lines + VAT), item table,
                              totals, footer VAT table, other charges, signature boxes,
                              stamps, handwritten notes. No pen marks.
 5. Mapping/WrappedRows       C# joins a wrapped description line the model returned as an
                              extra row (it repeats the row above's values); flagged for review
    Mapping/PartyResolver     C# decides supplier vs customer: VAT in suppliers.json, else
                              customer = labelled block ("Account No", "Bill to"...),
                              supplier = unlabelled block with a VAT number; a customer box
                              the model split in two is joined back (VAT, account number)
 6. PASS 2 (pen marks)        Vision/InkDetector: C# finds pen ink (coloured ink, and long
                              dark lines through the middle of a text line)
    Extraction/PenMarkPass    those strips, cut from a 300 DPI render and stacked into one
                              image; the prompt lists pass 1's rows by number; the model
                              returns only the changes (row, kind, column, old, new, initials)
    Mapping/PenMarkMerger     C# checks each change: row number confirmed by its text,
                              column found from the crossed-out value
 7. Mapping/ColumnMapper      printed column headers → our fields, using column-mappings.json
 8. Rules/ReviewRules         signature rules, required header fields, unmapped columns,
                              totals reconciliation, "pen marks not checked"
    Models/InvoiceResult      the final output
```

### Why two passes, and why C# decides the parties

On the sample invoices a 4B model reading the whole page copied every printed value
correctly but missed every pen mark (0 of 5), and swapped supplier and customer on every
run whatever the prompt said. Those are structural problems, so the fixes are structural:

- **Parties:** the model copies each address block with its label; C# decides who is who
  (`suppliers.json` by VAT number first). The decision is explained in `Invoice.Parties.Decisions`.
- **Pen marks:** thin pen lines are a tiny part of a whole-page image. C# finds where the ink
  is (cheap and layout-independent, unlike a fixed "table band"), crops those rows from a
  sharper render, and asks the model only about them, with the row list from pass 1.
  Changes the merger cannot place become review flags; a skipped or failed pass 2 gives
  the flag "Pen marks were NOT checked", so a miss is never silent.

Tune detection without the model: `dotnet run -- <file> --ink-only` saves the stacked
images (`page-N.pen.png`) and prints each ink band.

## Known suppliers

`suppliers.json` lists suppliers by VAT number. A block whose VAT number is listed is the
supplier wherever it is printed, and its name comes from this file. Add a supplier once its
VAT number is confirmed. Copied next to the executable, so it can be edited without recompiling.

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
ollama list                   # the model you pass with --model must be there (default qwen3-vl:4b)
dotnet build
dotnet run -- samples\Scan_For_Loui_1.pdf
dotnet run -- samples\scan_invoice_with_price_change_2.pdf
```

Options: `--model`, `--dpi` (default 150), `--password`, `--ctx` (default 12288),
`--max-tokens` (default 4096), `--timeout` minutes per page (default 40; a CPU-only run of the 4B model takes 20+ min per page), `--think`, `--quiet`,
`--keep-loaded`, `--out`, `--no-pen-pass`, `--ink-only`. Unknown options are rejected.
Press Ctrl+C to stop cleanly; the model is unloaded at the end.

Exit codes: `0` every page extracted; `1` stopped (bad input, wrong PDF password, Ollama
error, Ctrl+C); `2` finished, but at least one page has no result (timeout, cut off, invalid JSON).

## Output (per run, in `output\<file>_<model>_<timestamp>\`)

- `page-1.png`          what the model saw
- `schema.json`         the contract sent to the model
- `page-1.raw.json`     the model's raw answer (verbatim table)
- `page-1.partial.txt`  what was generated before a timeout or Ctrl+C
- `page-1.pen.png`      the stacked ink strips sent to pass 2 (`page-1.pen-K.png` if split)
- `page-1.pen.json`     ink bands found, and the changes the model reported
- `page-1.pen.raw.txt`  pass 2's raw answer, saved even when it timed out or was cut off
- `page-1.json`         parties (with how they were decided), mapped line items with pen marks,
                        footer, column mapping report, reconciliation and review flags

## Expected results on the samples (ground truth)

| | Scan_For_Loui_1 | scan_invoice_with_price_change_2 |
| --- | --- | --- |
| Full strikeouts | .N13 Linguine, Valor 400342 almonds | .N5 Spaghetti, .N50 Rigatoni, Valor 400342 almonds |
| Partial strikeouts | Panfette total 6.50 → 7; .N54 Riccioli expiry 06/07/29 → 09/10/30; .N88 +VAT 1.80 → 2 | Ballara total 8.00 → 10.00 |
| Signature by each change | Yes (initials) | Spaghetti and Rigatoni: none (should be flagged) |
| Stamp | Il-Mosta, PO 1321591, received 28/8/26, signed | same |
| Handwritten notes | €71.31 by the totals | €71.31 by the totals; "This is a handwritten note" at the bottom |
| Subtotal | 91.81 | 91.81 |
| Supplier | J.Calleja Import & Export Ltd, Y & P Complex, 5-6 Burmarrad Road, Naxxar NXR6345, VAT MT10062724 | same |
| Customer | Account SGM008, MOSTA, VAT MT16820514 (`AccountNumber`, `Name`, `VatNumber`) | same |

### Measured results

| Run | Printed values | Parties | Pen marks | Notes | Time (CPU) |
| --- | --- | --- | --- | --- | --- |
| Sample 2, 2026-11-09 21:07, combined build | 13/13 rows right + 1 extra row (wrapped line) | supplier right; customer VAT lost (box split) | 0/4: ink found all 4, pass 2 hit its 1200-token cap | 1 of 2 (€71.31 missed) | 15.5 + 12.5 min |

| Sample 2, 2026-11-10 00:21, after the four fixes | 13/13 rows right, sums to 91.81 | both right (supplier address, customer VAT + account) | 0/4 right: model listed rows 1–8 in order (8 = cap), 5 false changes, Valor missed | 1 of 2 (€71.31 missed) | 16 + 9.5 min |

Fixed after the 21:07 run: wrapped-row join, split customer box, supplier label kept in the address,
pass 2 limited to 8 marks with a 2000-token cap and its raw answer saved. Replaying that run's
pass 1 answer through the new C# gives 13 rows summing to 91.81 and both parties right.

## Known limits / next steps

- Pen marks: C# finds where to look; whether the 4B model then reads the strips correctly
  is not yet measured. Compare `qwen3-vl:8b` / `30b` / a hosted model.
- A black-pen correction that is only a short scribble (no line through text) is not found
  by the ink detector, so pass 2 never sees it. Coloured ink is always found.
- Some ink bands are not in the item table (stamp, footer, stray marks); they are sent too,
  and the prompt tells the model to ignore them.
- Ollama on this laptop runs on CPU only (it does not detect the NVIDIA GPU), which is why
  a page takes 20+ minutes; check `%LOCALAPPDATA%\Ollama\server.log` for "inference compute".
- The default `qwen3-vl:4b` tag may think before answering (slow, uses the token budget).
  If an instruct-only tag is available in Ollama, try it with `--model`.
- No bounding boxes yet, so "signature nearby" is the model's judgement, not a measured distance.
