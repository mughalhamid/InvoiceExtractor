using System.Text.RegularExpressions;
using InvoiceExtractor.Models;

namespace InvoiceExtractor.Mapping;

/// <summary>
/// Places the changes the model reported in pass 2 onto pass 1's rows, in plain C#:
///  - Row: the model's RowNumber, accepted only if its RowText matches that row's printed cells;
///    otherwise the row whose cells best match RowText. Never a guess.
///  - Column: the cell in that row equal to the crossed-out value; the model's column is used
///    only to break a tie (e.g. "1.80" printed in two columns) or when no cell matches.
/// Changes it cannot place are returned as notes, so they become review flags, never silently lost.
/// </summary>
public static class PenMarkMerger
{
    public static (Dictionary<int, RowMarks> Rows, List<string> Notes) Merge(
        IReadOnlyList<string> headers, IReadOnlyList<TableRow> rows, IEnumerable<PenMark> marks)
    {
        var result = new Dictionary<int, RowMarks>();   // key: 0-based row index
        var notes = new List<string>();

        foreach (var mark in marks)
        {
            var what = $"{mark.Kind} on row {mark.RowNumber} (\"{mark.RowText}\")";
            var r = FindRow(rows, mark, out var rowNote);
            if (r < 0)
            {
                notes.Add($"Pen mark {what}: {rowNote} Check it by hand.");
                continue;
            }
            if (rowNote is not null) notes.Add($"Pen mark {what}: {rowNote}");

            var current = result.GetValueOrDefault(r) ?? RowMarks.None;
            if (mark.Kind == PenMarkKind.FullStrikeout)
            {
                result[r] = current with
                {
                    IsFullyStruckOut = true,
                    HasSignatureNearStrikeout = current.HasSignatureNearStrikeout || mark.HasSignatureNearby
                };
                continue;
            }

            if (mark.OriginalValue is null && mark.NewValue is null) continue;
            var column = FindColumn(headers, rows[r].Cells ?? [], mark, out var columnNote);
            if (columnNote is not null) notes.Add($"Row {r + 1}: {columnNote}");

            var corrections = current.Corrections.Where(c => !SameColumn(c.Column, column)).ToList();
            corrections.Add(new Correction(column, mark.OriginalValue, mark.NewValue, mark.HasSignatureNearby));
            result[r] = current with { Corrections = corrections };
        }

        return (result, notes);
    }

    private static int FindRow(IReadOnlyList<TableRow> rows, PenMark mark, out string? note)
    {
        note = null;
        var wanted = Tokens(mark.RowText);
        var index = mark.RowNumber - 1;

        // RowNumber, confirmed by the description (or accepted as-is if the model gave no text)
        if (index >= 0 && index < rows.Count && (wanted.Count == 0 || Score(wanted, rows[index]) >= 0.5))
            return index;

        // Otherwise: the single best text match
        if (wanted.Count == 0)
        {
            note = $"row number {mark.RowNumber} does not exist and no row text was given.";
            return -1;
        }
        int best = -1;
        double bestScore = 0;
        bool tie = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var s = Score(wanted, rows[i]);
            if (s > bestScore + 1e-9) { best = i; bestScore = s; tie = false; }
            else if (s > 0 && Math.Abs(s - bestScore) < 1e-9) tie = true;
        }
        if (bestScore < 0.5) { note = "no item row matches its text."; return -1; }
        if (tie) { note = "its text matches more than one item row."; return -1; }

        note = $"placed on row {best + 1} by its text (the model said row {mark.RowNumber}).";
        return best;
    }

    private static double Score(HashSet<string> wanted, TableRow? row) =>
        wanted.Count(Tokens(string.Join(' ', row?.Cells ?? [])).Contains) / (double)wanted.Count;

    private static string FindColumn(IReadOnlyList<string> headers, List<string?> cells, PenMark mark, out string? note)
    {
        note = null;
        var candidates = new List<int>();
        if (mark.OriginalValue is not null)
            for (var c = 0; c < headers.Count && c < cells.Count; c++)
                if (SameValue(cells[c], mark.OriginalValue)) candidates.Add(c);

        var modelColumn = headers.ToList().FindIndex(h => SameColumn(h, mark.Column));

        if (candidates.Count == 1) return headers[candidates[0]];
        if (candidates.Count > 1)
        {
            if (candidates.Contains(modelColumn)) return headers[modelColumn];
            note = $"crossed-out value '{mark.OriginalValue}' is printed in columns " +
                   $"{string.Join(", ", candidates.Select(c => headers[c]))}; assumed {headers[candidates[0]]}.";
            return headers[candidates[0]];
        }
        if (modelColumn >= 0)
        {
            if (mark.OriginalValue is not null)
                note = $"crossed-out value '{mark.OriginalValue}' is not in the row as read in pass 1; " +
                       $"column {headers[modelColumn]} is the model's choice.";
            return headers[modelColumn];
        }
        note = $"could not tell which column '{mark.OriginalValue}' -> '{mark.NewValue}' belongs to.";
        return mark.Column ?? "?";
    }

    private static bool SameColumn(string? a, string? b) =>
        a is not null && b is not null && ColumnMapper.Normalize(a) == ColumnMapper.Normalize(b);

    /// <summary>Equal as numbers when both look numeric ("6.50" = "6.5"), else equal ignoring spaces and case.</summary>
    private static bool SameValue(string? cell, string value)
    {
        if (cell is null) return false;
        if (LooksNumeric(cell) && LooksNumeric(value) &&
            ColumnMapper.ParseAmount(cell) is { } a && ColumnMapper.ParseAmount(value) is { } b)
            return a == b;
        return Squash(cell) == Squash(value);
    }

    private static bool LooksNumeric(string s) => Regex.IsMatch(s.Trim(), @"^[^\d/]{0,2}[\d\.,\-]+[a-zA-Z€]{0,3}$");
    private static string Squash(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s", "");

    private static HashSet<string> Tokens(string? s) =>
        s is null ? [] : Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]{2,}").Select(m => m.Value).ToHashSet();
}
