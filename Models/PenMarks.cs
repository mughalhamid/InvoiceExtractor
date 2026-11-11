namespace InvoiceExtractor.Models;

// ---------------------------------------------------------------------------
// PASS 2: what the model returns for the pen-mark image. That image is made of
// enlarged strips of the page where C# found pen ink (Vision/InkDetector.cs), and the
// prompt lists pass 1's rows by number. The model only reports CHANGES; C# then
// checks each against the rows and columns (Mapping/PenMarkMerger.cs).
// ---------------------------------------------------------------------------

public record PenMarkReading(List<PenMark> Marks);

public enum PenMarkKind
{
    FullStrikeout,     // a pen line through the whole item row
    PartialStrikeout   // one printed value crossed out, usually with a new value written by hand
}

public record PenMark(
    int RowNumber,              // the row's number in the list given in the prompt
    string? RowText,            // the row's printed description, used to double-check RowNumber
    PenMarkKind Kind,
    string? Column,             // printed header of the changed column (partial strikeout only)
    string? OriginalValue,      // the crossed-out printed value
    string? NewValue,           // the handwritten replacement, null if none
    bool HasSignatureNearby);   // initials / signature / paraph right next to this change

/// <summary>A partial strikeout placed on a row by C#.</summary>
public record Correction(
    string Column,
    string? OriginalValue,
    string? NewValue,
    bool HasSignatureNearby);

/// <summary>All pen marks C# placed on one item row.</summary>
public record RowMarks(
    bool IsFullyStruckOut,
    bool HasSignatureNearStrikeout,
    List<Correction> Corrections)
{
    public static RowMarks None => new(false, false, []);
}
