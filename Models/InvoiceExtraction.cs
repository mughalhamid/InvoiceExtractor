namespace InvoiceExtractor.Models;

// ---------------------------------------------------------------------------
// What the MODEL returns. These records are turned into a JSON schema and sent
// to the model, which is forced to answer in exactly this shape.
//
// Design choices:
//  - The item table is read VERBATIM: the printed headers once, then each row's
//    cells in the same order. The model does not decide what a column means;
//    C# does that later (see Mapping/ColumnMapper.cs). This avoids guessing and
//    copes with headers that change between suppliers.
//  - Amounts are strings, copied exactly as printed/written ("8.00", "19.500E").
//    Parsing and maths happen in C#, never inside the model.
//  - Every list has a maximum length in the schema (Extraction/InvoiceSchema.cs),
//    so a small model cannot loop forever adding items.
//  - No bounding boxes yet: a 4B local model gives unreliable coordinates.
// ---------------------------------------------------------------------------

public record InvoiceExtraction(
    Party Supplier,
    Party Customer,
    string? InvoiceNumber,
    string? InvoiceDate,
    string? Currency,
    ItemTable ItemTable,
    Totals Totals,
    List<Stamp> Stamps,
    List<HandwrittenNote> HandwrittenNotes);

public record Party(
    string? Name,
    string? Address,
    string? VatNumber);

/// <summary>The line-item table exactly as printed.</summary>
public record ItemTable(
    List<string> Headers,       // column headers left to right, e.g. ["Quantity","Description","Expiry",...]
    List<TableRow> Rows);

public record TableRow(
    List<string?> Cells,        // one value per header, same order; null if empty
    bool IsFullyStruckOut,
    bool HasSignatureNearStrikeout,
    List<Correction> Corrections);

/// <summary>A partial strikeout: one printed value crossed out and (usually) a new value written by hand.</summary>
public record Correction(
    string Column,              // the printed header of the changed column, e.g. "Total" or "Expiry"
    string? OriginalValue,      // the struck-out printed value
    string? NewValue,           // the handwritten replacement, null if none
    bool HasSignatureNearby);

public record Totals(
    string? Subtotal,
    string? VatAmount,
    string? Total);

public record Stamp(
    string? Text,               // printed text of the stamp, e.g. "Il-Mosta"
    string? PoNumber,
    string? DateReceived,
    bool HasSignature);

public record HandwrittenNote(
    string Text,
    string? Location);          // e.g. "next to totals", "bottom of page"
