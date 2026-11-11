using System.Text.Json;
using System.Text.RegularExpressions;
using InvoiceExtractor.Models;

namespace InvoiceExtractor.Mapping;

/// <summary>
/// Decides which address block is the supplier and which is the customer, in plain C#.
/// The model only copies the blocks verbatim; asking it to decide gave a stable swap
/// on the sample invoices whatever the prompt said.
///
/// Rules, in order:
///  1. Supplier = the block whose VAT number is in suppliers.json (a known supplier).
///  2. Customer = a block LABELLED like a customer ("Account", "Customer", "Bill to", ...).
///  3. Otherwise supplier = an UNLABELLED block with a VAT number that is not the customer's.
/// A "label" that is not customer-style (the model often puts the first line there) is treated
/// as an address line, and a customer box the model split in two is joined back together.
/// Every decision is explained in the notes, so a wrong one is easy to spot.
/// </summary>
public sealed class PartyResolver
{
    private record KnownSupplier(string Name, List<string> VatNumbers);
    private record SupplierFile(List<KnownSupplier> Suppliers);

    private static readonly Regex CustomerLabel = new(
        @"\b(account|acc\.?\s*no|a/c|customer|client|bill(ed)?\s*to|invoice\s*to|sold\s*to|deliver(y|ed)?\s*to|ship\s*to)\b",
        RegexOptions.IgnoreCase);

    // EU-style VAT number: 2-letter country prefix + 8-12 digits/letters, e.g. MT10062724
    private static readonly Regex VatPattern = new(@"\b([A-Z]{2})\s?([0-9A-Z]{8,12})\b");

    // Lines that are not part of a postal address
    private static readonly Regex NonAddressLine = new(
        @"\b(vat|tax|tel|phone|fax|mob|e-?mail|www|bcrs|account|acc\.?\s*no|a/c|reg\.?\s*no)\b",
        RegexOptions.IgnoreCase);

    private readonly List<KnownSupplier> _known;

    private PartyResolver(List<KnownSupplier> known) => _known = known;

    public static PartyResolver Load(string path)
    {
        if (!File.Exists(path)) return new PartyResolver([]);
        var file = JsonSerializer.Deserialize<SupplierFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new PartyResolver(file?.Suppliers ?? []);
    }

    public record Result(Party Supplier, Party Customer, List<string> Notes);

    public Result Resolve(string? logoText, IEnumerable<AddressBlock?>? blocks)
    {
        var notes = new List<string>();
        // The model often puts a block's first printed line in Label ("Y & P Complex", "MOSTA").
        // Only a customer-style label is a real label; any other "label" is an address line.
        var list = (blocks ?? []).Where(b => b is not null).Select(b => WithLabelAsLine(b!)).ToList();
        var vats = list.Select(VatOf).ToList();

        // 2. Customer: a labelled block (label, or a label-like first line)
        var customerIndex = list.FindIndex(IsCustomerLabelled);

        // 1. Supplier by known VAT number
        int supplierIndex = -1;
        KnownSupplier? known = null;
        for (var i = 0; i < list.Count && known is null; i++)
        {
            known = _known.FirstOrDefault(k => vats[i] is { } v && k.VatNumbers.Any(n => NormalizeVat(n) == v));
            if (known is not null) supplierIndex = i;
        }
        if (known is not null)
            notes.Add($"Supplier identified by VAT number {vats[supplierIndex]} (known supplier in suppliers.json).");

        // 3. Supplier = unlabelled block with a VAT number that is not the customer's
        if (supplierIndex < 0)
        {
            supplierIndex = list.FindIndex(b => b != list.ElementAtOrDefault(customerIndex) &&
                                                !IsCustomerLabelled(b) &&
                                                VatOf(b) is not null);
            if (supplierIndex >= 0)
                notes.Add("Supplier identified as the unlabelled address block with a VAT number " +
                          "(not in suppliers.json: add it once confirmed).");
        }

        if (supplierIndex >= 0 && supplierIndex == customerIndex)
        {
            notes.Add("The known supplier's VAT number is in a block labelled like a customer; check the parties by hand.");
            customerIndex = -1;
        }
        // Customer fallback: another block with a VAT number
        if (customerIndex < 0)
            customerIndex = list.FindIndex(b => b != list.ElementAtOrDefault(supplierIndex) && VatOf(b) is not null);

        if (supplierIndex < 0) notes.Add("Could not tell which address block is the supplier.");
        if (customerIndex < 0) notes.Add("Could not tell which address block is the customer.");
        if (supplierIndex >= 0 && customerIndex >= 0 && vats[supplierIndex] is { } sv && sv == vats[customerIndex])
            notes.Add($"Supplier and customer have the same VAT number {sv}; check the parties by hand.");

        var supplierBlock = list.ElementAtOrDefault(supplierIndex);
        var customerBlock = list.ElementAtOrDefault(customerIndex);

        // The model sometimes splits one customer box into two blocks ("Account No: SGM008 / MOSTA"
        // and "MOSTA / VAT NO.:MT16820514"), losing the VAT number. Every other block that is not
        // the supplier and has no customer-style label of its own is joined to the customer.
        // A separately labelled block ("Deliver to") stays apart.
        if (customerBlock is not null)
        {
            var extras = list.Where((b, i) => i != supplierIndex && i != customerIndex && !IsCustomerLabelled(b)).ToList();
            if (extras.Count > 0)
            {
                customerBlock = customerBlock with
                {
                    Lines = [.. customerBlock.Lines ?? [], .. extras.SelectMany(b => b.Lines ?? [])],
                    VatNumber = customerBlock.VatNumber ?? extras.Select(b => b.VatNumber).FirstOrDefault(v => v is not null)
                };
                notes.Add($"Joined {extras.Count} more block(s) to the customer (the customer box was split): " +
                          string.Join("; ", extras.Select(b => string.Join(" / ", b.Lines ?? []))) + ".");
            }
        }

        var supplier = new Party(
            Name: known?.Name ?? NullIfBlank(logoText) ?? supplierBlock?.Lines?.FirstOrDefault(),
            Address: AddressOf(supplierBlock),
            VatNumber: supplierBlock is null ? null : VatOf(supplierBlock));

        var customer = new Party(
            Name: customerBlock?.Lines?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !NonAddressLine.IsMatch(l))?.Trim(),
            Address: AddressOf(customerBlock),
            VatNumber: customerBlock is null ? null : VatOf(customerBlock),
            AccountNumber: customerBlock is null ? null : AccountOf(customerBlock));

        return new Result(supplier, customer, notes);
    }

    private static readonly Regex AccountPattern = new(
        @"\b(?:account|acc\.?|a/c)\s*(?:no\.?|number|#)?\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/]{2,})",
        RegexOptions.IgnoreCase);

    private static bool IsCustomerLabelled(AddressBlock b) =>
        CustomerLabel.IsMatch(b.Label ?? "") || CustomerLabel.IsMatch(b.Lines?.FirstOrDefault() ?? "");

    /// <summary>A label that is not customer-style is really the block's first line.</summary>
    private static AddressBlock WithLabelAsLine(AddressBlock b) =>
        string.IsNullOrWhiteSpace(b.Label) || CustomerLabel.IsMatch(b.Label)
            ? b
            : b with { Label = null, Lines = [b.Label.Trim(), .. b.Lines ?? []] };

    private static string? AccountOf(AddressBlock b)
    {
        foreach (var text in new[] { b.Label }.Concat(b.Lines ?? []))
            if (text is not null && AccountPattern.Match(text) is { Success: true } m)
                return m.Groups[1].Value;
        return null;
    }

    /// <summary>The block's VAT number: the copied field, else one found in its lines. Normalized.</summary>
    private static string? VatOf(AddressBlock b)
    {
        foreach (var text in new[] { b.VatNumber }.Concat(b.Lines ?? []))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = VatPattern.Match(text.ToUpperInvariant().Replace(".", " "));
            if (m.Success) return NormalizeVat(m.Value);
        }
        return null;
    }

    public static string NormalizeVat(string vat) => Regex.Replace(vat.ToUpperInvariant(), @"[\s\.\-]", "");

    /// <summary>Postal lines only: drops VAT/Tel/account lines and lone punctuation.</summary>
    private static string? AddressOf(AddressBlock? b)
    {
        if (b?.Lines is null) return null;
        var lines = b.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l) && Regex.IsMatch(l, "[A-Za-z0-9]") && !NonAddressLine.IsMatch(l))
            .Select(l => l.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)   // joined blocks can repeat a town line
            .ToList();
        return lines.Count == 0 ? null : string.Join(", ", lines);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
