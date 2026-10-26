using InvoiceExtractor.Models;

namespace InvoiceExtractor.Rules;

/// <summary>
/// Business rules in plain C#. The model perceives; code decides.
/// v1: the signature rule from the task description.
/// Later: totals reconciliation, VAT number format, date sanity.
/// </summary>
public static class ReviewRules
{
    public static List<string> Evaluate(InvoiceExtraction invoice)
    {
        var flags = new List<string>();

        for (var i = 0; i < invoice.LineItems.Count; i++)
        {
            var line = invoice.LineItems[i];
            var label = $"Line {i + 1} ({line.Description ?? "no description"})";

            if (line.IsFullyStruckOut && !line.HasSignatureNearStrikeout)
                flags.Add($"{label}: fully struck out without a signature nearby.");

            foreach (var c in line.Corrections ?? new List<Correction>())
            {
                if (!c.HasSignatureNearby)
                    flags.Add($"{label}: {c.Field} changed '{c.OriginalValue}' -> '{c.NewValue}' without a signature nearby.");
            }
        }

        foreach (var stamp in invoice.Stamps ?? new List<Stamp>())
        {
            if (!stamp.HasSignature)
                flags.Add($"Stamp '{stamp.Text}' is not signed.");
        }

        if (string.IsNullOrWhiteSpace(invoice.Supplier?.VatNumber))
            flags.Add("Supplier VAT number not found.");

        return flags;
    }
}
