namespace InvoiceExtractor.Models;

// ---------------------------------------------------------------------------
// What the APP returns: pass 1's printed content after C# has decided the parties,
// mapped the printed columns to our fixed fields, placed pass 2's pen marks and
// applied the business rules. This is the shape AutomateAP would eventually consume.
// ---------------------------------------------------------------------------

public record InvoiceResult(
    Party Supplier,
    Party Customer,
    string? InvoiceNumber,
    string? InvoiceDate,
    string? Currency,
    List<LineItem> LineItems,
    Totals Totals,
    PrintedTable VatSummary,                     // footer VAT table, as printed
    List<Charge> OtherCharges,                   // e.g. BCRS
    List<Signature> Signatures,
    List<Stamp> Stamps,
    List<HandwrittenNote> HandwrittenNotes,
    ColumnMappingReport ColumnMapping,
    PartyReport Parties,
    bool PenMarksChecked,                        // false if pass 2 was skipped or failed
    Reconciliation? Reconciliation,
    List<string> ReviewFlags);

public record LineItem(
    int RowNumber,
    string? Description,
    string? Quantity,
    string? UnitPrice,
    string? VatRate,
    string? VatAmount,
    string? TotalExclVat,
    string? TotalInclVat,
    string? Expiry,
    Dictionary<string, string?> OtherColumns,   // printed columns we have no field for yet
    bool IsFullyStruckOut,
    bool HasSignatureNearStrikeout,
    List<FieldCorrection> Corrections);

/// <summary>A correction linked to one of our fields (or to the printed header if unmapped).</summary>
public record FieldCorrection(
    string Field,
    string PrintedColumn,
    string? OriginalValue,
    string? NewValue,
    bool HasSignatureNearby);

/// <summary>Which printed header went to which field, so a wrong mapping is easy to spot.</summary>
public record ColumnMappingReport(
    Dictionary<string, string> Mapped,          // printed header -> field
    List<string> Unmapped);                      // printed headers with no known meaning

/// <summary>The blocks as the model copied them, and how C# chose supplier and customer.</summary>
public record PartyReport(
    string? LogoText,
    List<AddressBlock> AddressBlocks,
    List<string> Decisions);

/// <summary>Arithmetic done in C#, never by the model.</summary>
public record Reconciliation(
    decimal SumOfPrintedLines,
    decimal? PrintedSubtotal,
    bool PrintedLinesMatchSubtotal,
    decimal AdjustedTotal,                       // line totals after removing struck-out rows and applying corrections
    List<string> Notes);
