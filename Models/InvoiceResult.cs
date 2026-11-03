namespace InvoiceExtractor.Models;

// ---------------------------------------------------------------------------
// What the APP returns: the model's raw extraction after C# has mapped the
// printed columns to our fixed fields and applied the business rules.
// This is the shape AutomateAP would eventually consume.
// ---------------------------------------------------------------------------

public record InvoiceResult(
    Party Supplier,
    Party Customer,
    string? InvoiceNumber,
    string? InvoiceDate,
    string? Currency,
    List<LineItem> LineItems,
    Totals Totals,
    List<Stamp> Stamps,
    List<HandwrittenNote> HandwrittenNotes,
    ColumnMappingReport ColumnMapping,
    Reconciliation? Reconciliation,
    List<string> ReviewFlags);

public record LineItem(
    int RowNumber,
    string? Description,
    string? Quantity,
    string? UnitPrice,
    string? VatRate,
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

/// <summary>Arithmetic done in C#, never by the model.</summary>
public record Reconciliation(
    decimal SumOfPrintedLines,
    decimal? PrintedSubtotal,
    bool PrintedLinesMatchSubtotal,
    decimal AdjustedTotal,                       // after removing struck-out rows and applying corrections
    List<string> Notes);
