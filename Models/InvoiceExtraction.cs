namespace InvoiceExtractor.Models;

// ---------------------------------------------------------------------------
// The output contract. These records are turned into a JSON schema and sent to
// the model, which is forced to answer in exactly this shape.
//
// Design choices for v1:
//  - Amounts are strings, copied exactly as printed/written ("8.00", "10.00").
//    Parsing and maths happen later in C# rules, never inside the model.
//  - No bounding boxes yet: a 4B local model gives unreliable coordinates.
//    We rely on "HasSignatureNearby" for now and add geometry in a later step.
//  - Keep it small. Small models get worse as the schema grows.
// ---------------------------------------------------------------------------

public record InvoiceExtraction(
    Party Supplier,
    Party Customer,
    string? InvoiceNumber,
    string? InvoiceDate,
    string? Currency,
    List<LineItem> LineItems,
    Totals Totals,
    List<Stamp> Stamps,
    List<HandwrittenNote> HandwrittenNotes,
    List<Signature> Signatures,
    string? ModelComments);

public record Party(
    string? Name,
    string? Address,
    string? VatNumber);

public record LineItem(
    string? Description,
    string? Quantity,
    string? UnitPrice,
    string? VatRate,
    string? TotalExclVat,
    string? TotalInclVat,
    bool IsFullyStruckOut,
    bool HasSignatureNearStrikeout,
    List<Correction> Corrections);

/// <summary>A partial strikeout: one value crossed out and (usually) a new value written by hand.</summary>
public record Correction(
    string Field,              // which column changed, e.g. "TotalExclVat", "ExpiryDate"
    string? OriginalValue,     // the struck-out printed value
    string? NewValue,          // the handwritten replacement, null if none
    bool HasSignatureNearby);

public record Totals(
    string? Subtotal,
    string? VatAmount,
    string? Total);

public record Stamp(
    string? Text,              // printed text of the stamp, e.g. "Il-Mosta"
    string? PoNumber,
    string? DateReceived,
    bool HasSignature);

public record HandwrittenNote(
    string Text,
    string? Location);         // e.g. "next to totals", "bottom of page"

public record Signature(
    string? Location,          // e.g. "inside stamp", "right of line 3"
    string? AssociatedWith);   // what it is signing, e.g. "stamp", "line 3 correction"
