using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceExtractor.Models;

namespace InvoiceExtractor.Mapping;

/// <summary>
/// Turns the verbatim table the model read into our fixed line-item fields.
/// The model only READS; this deterministic code decides what each column MEANS,
/// using column-mappings.json. Unknown headers are kept and reported, never guessed.
/// </summary>
public sealed class ColumnMapper
{
    private readonly Dictionary<string, string> _headerToField;

    private ColumnMapper(Dictionary<string, string> headerToField) => _headerToField = headerToField;

    public static ColumnMapper Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, string>();
        foreach (var field in doc.RootElement.EnumerateObject())
        {
            if (field.Name.StartsWith('_') || field.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var header in field.Value.EnumerateArray())
                map[Normalize(header.GetString() ?? "")] = field.Name;
        }
        return new ColumnMapper(map);
    }

    /// <summary>"Unit Price:" -> "unitprice", "+ VAT" -> "+vat" (the + is kept on purpose).</summary>
    public static string Normalize(string? header) =>
        Regex.Replace((header ?? "").ToLowerInvariant(), @"[\s\.:]", "");

    // The schema allows null list entries; treat them as absent instead of crashing.
    // Pass 2 and the merger use these same lists, so row numbers line up everywhere.
    public static List<string> ItemHeaders(InvoiceExtraction x) =>
        (x.ItemTable?.Headers ?? []).Select(h => h ?? "").ToList();

    public static List<TableRow> ItemRows(InvoiceExtraction x) =>
        (x.ItemTable?.Rows ?? []).Where(r => r is not null).ToList();

    /// <summary>Short printed text that identifies a row for the pen-mark prompt (the description cell if mapped).</summary>
    public string RowLabel(IReadOnlyList<string> headers, TableRow row)
    {
        var cells = row.Cells ?? [];
        for (var c = 0; c < headers.Count && c < cells.Count; c++)
            if (_headerToField.GetValueOrDefault(Normalize(headers[c])) == "Description" && !string.IsNullOrWhiteSpace(cells[c]))
                return cells[c]!;
        return string.Join(" | ", cells.Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    public InvoiceResult Map(InvoiceExtraction x, PartyResolver.Result parties,
        IReadOnlyDictionary<int, RowMarks>? penMarks, bool penMarksChecked)
    {
        var headers = ItemHeaders(x);
        var rows = ItemRows(x);

        // Decide each column's field once. If two columns claim the same field, the first wins.
        var columnField = new string?[headers.Count];
        var mapped = new Dictionary<string, string>();
        var unmapped = new List<string>();
        for (var c = 0; c < headers.Count; c++)
        {
            if (_headerToField.TryGetValue(Normalize(headers[c]), out var field) &&
                !mapped.ContainsValue(field) && !mapped.ContainsKey(headers[c]))
            {
                columnField[c] = field;
                mapped[headers[c]] = field;
            }
            else
            {
                unmapped.Add(headers[c]);
            }
        }

        var lineItems = new List<LineItem>();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = row.Cells ?? [];
            var values = new Dictionary<string, string?>();
            var other = new Dictionary<string, string?>();

            for (var c = 0; c < headers.Count; c++)
            {
                var value = c < cells.Count ? cells[c] : null;
                if (columnField[c] is { } field) values[field] = value;
                else other[headers[c]] = value;
            }

            var marks = penMarks?.GetValueOrDefault(r) ?? RowMarks.None;
            var corrections = marks.Corrections
                .Select(k => new FieldCorrection(
                    Field: FieldOfColumn(headers, columnField, k.Column),
                    PrintedColumn: k.Column ?? "?",
                    k.OriginalValue, k.NewValue, k.HasSignatureNearby))
                .ToList();

            lineItems.Add(new LineItem(
                RowNumber: r + 1,
                Description: values.GetValueOrDefault("Description"),
                Quantity: values.GetValueOrDefault("Quantity"),
                UnitPrice: values.GetValueOrDefault("UnitPrice"),
                VatRate: values.GetValueOrDefault("VatRate"),
                VatAmount: values.GetValueOrDefault("VatAmount"),
                TotalExclVat: values.GetValueOrDefault("TotalExclVat"),
                TotalInclVat: values.GetValueOrDefault("TotalInclVat"),
                Expiry: values.GetValueOrDefault("Expiry"),
                OtherColumns: other,
                IsFullyStruckOut: marks.IsFullyStruckOut,
                HasSignatureNearStrikeout: marks.HasSignatureNearStrikeout,
                Corrections: corrections));
        }

        return new InvoiceResult(
            parties.Supplier, parties.Customer, x.InvoiceNumber, x.InvoiceDate, x.Currency,
            lineItems, x.Totals,
            new PrintedTable(
                (x.VatSummary?.Headers ?? []).Select(h => h ?? "").ToList(),
                (x.VatSummary?.Rows ?? []).Where(r => r is not null).ToList()),
            (x.OtherCharges ?? []).Where(v => v is not null).ToList(),
            (x.Signatures ?? []).Where(s => s is not null).ToList(),
            (x.Stamps ?? []).Where(s => s is not null).ToList(),
            (x.HandwrittenNotes ?? []).Where(n => n is not null).ToList(),
            new ColumnMappingReport(mapped, unmapped),
            new PartyReport(x.LogoText, (x.AddressBlocks ?? []).Where(b => b is not null).ToList(), parties.Notes),
            penMarksChecked,
            Reconciliation: null,
            ReviewFlags: []);
    }

    /// <summary>
    /// The field a correction belongs to: the field of the column with that printed header,
    /// so a correction to a column that lost the "first wins" contest is not mistaken for it.
    /// </summary>
    private static string FieldOfColumn(List<string> headers, string?[] columnField, string? column)
    {
        var c = headers.FindIndex(h => Normalize(h) == Normalize(column));
        if (c >= 0 && columnField[c] is { } field) return field;
        return column ?? "?";
    }

    /// <summary>
    /// Parses printed amounts like "6.50", "19.500E", "€ 1,234.50", "6,50", "1.234,50". Null if not a number.
    /// </summary>
    public static decimal? ParseAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = Regex.Replace(s, @"[^\d\.,\-]", "");
        int lastComma = cleaned.LastIndexOf(','), lastDot = cleaned.LastIndexOf('.');

        if (lastComma > lastDot && Regex.IsMatch(cleaned, @",\d{1,2}$"))
            cleaned = cleaned.Replace(".", "").Replace(',', '.');   // decimal comma: "6,50", "1.234,50"
        else
            cleaned = cleaned.Replace(",", "");                      // thousands comma: "1,234.50"

        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
