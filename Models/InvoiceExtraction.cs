namespace InvoiceExtractor.Models;

// ---------------------------------------------------------------------------
// What the MODEL returns in PASS 1 (the whole page). These records are turned
// into a JSON schema and sent to the model, which is forced to answer in this shape.
//
// Design choices ("the model reads, C# decides"):
//  - Address blocks are copied VERBATIM with their printed label. The model does not
//    decide which block is the supplier; C# does (Mapping/PartyResolver.cs). Prompt
//    wording alone could not stop a 4B model swapping supplier and customer.
//  - The item table is read VERBATIM: the printed headers once, then each row's
//    cells in the same order. C# decides what each column means (Mapping/ColumnMapper.cs).
//  - No pen marks here: a 4B model reading the whole page missed every one of them.
//    Pass 2 looks only at the strips where C# found ink (Extraction/PenMarkPass.cs).
//  - Amounts are strings, copied exactly as printed ("8.00", "19.500E").
//    Parsing and maths happen in C#, never inside the model.
//  - Every list has a maximum length in the schema (Extraction/InvoiceSchema.cs),
//    so a small model cannot loop forever adding items.
// ---------------------------------------------------------------------------

public record InvoiceExtraction(
    string? LogoText,
    List<AddressBlock> AddressBlocks,
    string? InvoiceNumber,
    string? InvoiceDate,
    string? Currency,
    PrintedTable ItemTable,
    Totals Totals,
    PrintedTable VatSummary,
    List<Charge> OtherCharges,
    List<Signature> Signatures,
    List<Stamp> Stamps,
    List<HandwrittenNote> HandwrittenNotes);

/// <summary>A block of name/address lines exactly as printed, e.g. a letterhead or "Account No" box.</summary>
public record AddressBlock(
    string? Label,              // printed label of the block, e.g. "Account No: SGM008", "Bill to"; null if none
    List<string> Lines,         // every printed line of the block, top to bottom, including VAT/Tel lines
    string? VatNumber);         // the VAT number printed in this block, exactly as printed

/// <summary>A printed table exactly as printed: headers once, then one list of cells per row.</summary>
public record PrintedTable(
    List<string> Headers,       // column headers left to right
    List<TableRow> Rows);

public record TableRow(
    List<string?> Cells);       // one value per header, same order; null if empty

/// <summary>Party after C# has decided which address block is which (not filled by the model).</summary>
public record Party(
    string? Name,
    string? Address,
    string? VatNumber,
    string? AccountNumber = null);   // e.g. "SGM008" from "Account No: SGM008" (customer)

public record Totals(
    string? Subtotal,
    string? VatAmount,
    string? Total);

public record Charge(
    string? Label,              // e.g. "BCRS", "Discount"
    string? Amount);

/// <summary>A printed signature box or line OUTSIDE the stamps, e.g. "Received by ... Signature".</summary>
public record Signature(
    string? PrintedLabel,
    string? Location,
    bool IsSigned);

public record Stamp(
    string? Text,               // printed text of the stamp, e.g. "Il-Mosta"
    string? PoNumber,
    string? DateReceived,
    bool HasSignature);

public record HandwrittenNote(
    string Text,
    string? Location);          // e.g. "next to totals", "bottom of page"
