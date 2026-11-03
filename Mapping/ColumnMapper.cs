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
    public static string Normalize(string header) =>
        Regex.Replace(header.ToLowerInvariant(), @"[\s\.:]", "");

    public InvoiceResult Map(InvoiceExtraction x)
    {
        var headers = x.ItemTable?.Headers ?? new List<string>();
        var rows = x.ItemTable?.Rows ?? new List<TableRow>();

        // Decide each column's field once. If two columns claim the same field, the first wins.
        var columnField = new string?[headers.Count];
        var mapped = new Dictionary<string, string>();
        var unmapped = new List<string>();
        for (var c = 0; c < headers.Count; c++)
        {
            if (_headerToField.TryGetValue(Normalize(headers[c]), out var field) && !mapped.ContainsValue(field))
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
            var cells = row.Cells ?? new List<string?>();
            var values = new Dictionary<string, string?>();
            var other = new Dictionary<string, string?>();

            for (var c = 0; c < headers.Count; c++)
            {
                var value = c < cells.Count ? cells[c] : null;
                if (columnField[c] is { } field) values[field] = value;
                else other[headers[c]] = value;
            }

            var corrections = (row.Corrections ?? new List<Correction>())
                .Select(k => new FieldCorrection(
                    Field: _headerToField.TryGetValue(Normalize(k.Column ?? ""), out var f) ? f : k.Column ?? "?",
                    PrintedColumn: k.Column ?? "?",
                    k.OriginalValue, k.NewValue, k.HasSignatureNearby))
                .ToList();

            lineItems.Add(new LineItem(
                RowNumber: r + 1,
                Description: values.GetValueOrDefault("Description"),
                Quantity: values.GetValueOrDefault("Quantity"),
                UnitPrice: values.GetValueOrDefault("UnitPrice"),
                VatRate: values.GetValueOrDefault("VatRate"),
                TotalExclVat: values.GetValueOrDefault("TotalExclVat"),
                TotalInclVat: values.GetValueOrDefault("TotalInclVat"),
                Expiry: values.GetValueOrDefault("Expiry"),
                OtherColumns: other,
                IsFullyStruckOut: row.IsFullyStruckOut,
                HasSignatureNearStrikeout: row.HasSignatureNearStrikeout,
                Corrections: corrections));
        }

        return new InvoiceResult(
            x.Supplier, x.Customer, x.InvoiceNumber, x.InvoiceDate, x.Currency,
            lineItems, x.Totals,
            x.Stamps ?? new List<Stamp>(),
            x.HandwrittenNotes ?? new List<HandwrittenNote>(),
            new ColumnMappingReport(mapped, unmapped),
            Reconciliation: null,
            ReviewFlags: new List<string>(),
            x.ModelComments);
    }

    /// <summary>Parses printed amounts like "6.50", "19.500E", "€ 1,234.50". Null if not a number.</summary>
    public static decimal? ParseAmount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = Regex.Replace(s, @"[^\d\.,\-]", "").Replace(",", "");
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
