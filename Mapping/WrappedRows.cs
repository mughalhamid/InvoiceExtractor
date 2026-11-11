using InvoiceExtractor.Models;

namespace InvoiceExtractor.Mapping;

/// <summary>
/// Repairs one known misreading of pass 1: a description that wraps onto a second printed line
/// returned as an extra row. Seen on the samples: "NAS150G 3178" (2nd line of "VALOR 400342 ...")
/// came back as its own row with row 12's values copied, so the line totals exceeded the subtotal
/// by exactly that row's total.
///
/// A row is joined onto the row above it when either
///  - every cell equals the row above except ONE text cell (the copied-values case), or
///  - it has only ONE non-empty cell, which contains letters (a bare continuation line).
/// The text is appended to that cell of the row above. Every join is reported as a review flag,
/// because two genuinely different items with identical values would also match.
/// </summary>
public static class WrappedRows
{
    public static (List<TableRow> Rows, List<string> Notes) Merge(IReadOnlyList<TableRow> rows)
    {
        var result = new List<TableRow>();
        var notes = new List<string>();

        foreach (var row in rows)
        {
            var cells = row.Cells ?? [];
            var above = result.Count > 0 ? result[^1].Cells ?? [] : null;
            var column = above is null ? -1 : ContinuationColumn(above, cells);

            if (column < 0)
            {
                result.Add(new TableRow([.. cells]));
                continue;
            }

            var merged = new List<string?>(above!);
            while (merged.Count <= column) merged.Add(null);
            merged[column] = $"{merged[column]} {cells[column]}".Trim();
            result[^1] = new TableRow(merged);
            notes.Add($"Row {result.Count + 1} as read ('{cells[column]}') was treated as the second printed line " +
                      $"of row {result.Count} ('{above![column]}'), because it repeated that row's values; " +
                      "check that this is not a separate item.");
        }
        return (result, notes);
    }

    /// <summary>The column holding the continuation text, or -1 if this row is a real row.</summary>
    private static int ContinuationColumn(List<string?> above, List<string?> cells)
    {
        var count = Math.Max(above.Count, cells.Count);
        string? At(List<string?> l, int i) => i < l.Count ? l[i]?.Trim() : null;

        // Case 1: only one non-empty cell, and it is text
        var filled = Enumerable.Range(0, cells.Count).Where(i => !string.IsNullOrWhiteSpace(cells[i])).ToList();
        if (filled.Count == 1 && cells[filled[0]]!.Any(char.IsLetter) && !string.IsNullOrWhiteSpace(At(above, filled[0])))
            return filled[0];

        // Case 2: identical to the row above except for exactly one text cell, with at least two other values
        var differing = Enumerable.Range(0, count)
            .Where(i => !string.Equals(At(above, i), At(cells, i), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sameFilled = Enumerable.Range(0, count)
            .Count(i => !differing.Contains(i) && !string.IsNullOrWhiteSpace(At(cells, i)));
        if (differing.Count == 1 && sameFilled >= 2 &&
            At(cells, differing[0]) is { } text && text.Any(char.IsLetter) &&
            !string.IsNullOrWhiteSpace(At(above, differing[0])))
            return differing[0];

        return -1;
    }
}
