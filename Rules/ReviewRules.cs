using InvoiceExtractor.Mapping;
using InvoiceExtractor.Models;

namespace InvoiceExtractor.Rules;

/// <summary>
/// Business rules in plain C#. The model perceives; code decides.
/// </summary>
public static class ReviewRules
{
    public static InvoiceResult Apply(InvoiceResult invoice)
    {
        var flags = new List<string>();

        // Rule from the task: every change (full or partial strikeout) needs a signature close by.
        foreach (var line in invoice.LineItems)
        {
            var label = $"Line {line.RowNumber} ({line.Description ?? "no description"})";

            if (line.IsFullyStruckOut && !line.HasSignatureNearStrikeout)
                flags.Add($"{label}: fully struck out without a signature nearby.");

            foreach (var c in line.Corrections)
            {
                if (!c.HasSignatureNearby)
                    flags.Add($"{label}: {c.PrintedColumn} changed '{c.OriginalValue}' -> '{c.NewValue}' without a signature nearby.");
            }
        }

        foreach (var stamp in invoice.Stamps)
        {
            if (!stamp.HasSignature)
                flags.Add($"Stamp '{stamp.Text}' is not signed.");
        }

        if (string.IsNullOrWhiteSpace(invoice.Supplier?.VatNumber))
            flags.Add("Supplier VAT number not found.");

        if (invoice.ColumnMapping.Unmapped.Count > 0)
            flags.Add($"Columns with no agreed meaning (kept under OtherColumns): {string.Join(", ", invoice.ColumnMapping.Unmapped)}.");

        var reconciliation = Reconcile(invoice);
        if (reconciliation is { PrintedLinesMatchSubtotal: false, PrintedSubtotal: not null })
            flags.Add($"Printed line totals add up to {reconciliation.SumOfPrintedLines:0.00} but the subtotal is " +
                      $"{reconciliation.PrintedSubtotal:0.00}: a line was probably misread or missed.");

        return invoice with { ReviewFlags = flags, Reconciliation = reconciliation };
    }

    /// <summary>
    /// Checks the model's reading with arithmetic, and computes what is payable after
    /// struck-out rows are removed and corrections applied. Null if there is no total column.
    /// </summary>
    private static Reconciliation? Reconcile(InvoiceResult invoice)
    {
        if (!invoice.ColumnMapping.Mapped.ContainsValue("TotalExclVat")) return null;

        var notes = new List<string>();
        decimal printedSum = 0, adjusted = 0;

        foreach (var line in invoice.LineItems)
        {
            var printed = ColumnMapper.ParseAmount(line.TotalExclVat);
            if (printed is null)
            {
                notes.Add($"Line {line.RowNumber}: total '{line.TotalExclVat}' is not a number; skipped.");
                continue;
            }
            printedSum += printed.Value;

            if (line.IsFullyStruckOut) continue;

            var totalCorrection = line.Corrections.LastOrDefault(c => c.Field == "TotalExclVat");
            var corrected = ColumnMapper.ParseAmount(totalCorrection?.NewValue);
            if (corrected is not null)
                notes.Add($"Line {line.RowNumber}: total corrected {printed:0.00} -> {corrected:0.00}.");
            adjusted += corrected ?? printed.Value;
        }

        var subtotal = ColumnMapper.ParseAmount(invoice.Totals?.Subtotal);
        var matches = subtotal is null || Math.Abs(printedSum - subtotal.Value) <= 0.02m;

        return new Reconciliation(printedSum, subtotal, matches, adjusted, notes);
    }
}
