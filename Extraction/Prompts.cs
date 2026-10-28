namespace InvoiceExtractor.Extraction;

/// <summary>
/// The instructions sent with each page. Treat this as code: change one thing at a
/// time and re-run against the same invoices to see whether results improve.
/// </summary>
public static class Prompts
{
    public const string ExtractInvoice = """
        You are an expert accounts-payable clerk reading a scanned supplier invoice.
        Extract the data into the JSON schema provided. Follow these rules exactly.

        PARTIES
        - Supplier = the company that ISSUED the invoice. Its name is usually in the logo or letterhead.
          Its address, VAT number and phone are printed in the letterhead block, which may be on the
          left, right or top, and may be separate from the logo. This block has NO label like
          "Bill to" or "Account".
        - Customer = the company being billed. Its block is usually LABELLED, e.g. "Account No",
          "Customer", "Bill to", "Deliver to", "Sold to", and often has an account code.
        - Decide which block is which ONCE, using the labels, then do not revisit the decision.
        - An invoice normally shows BOTH VAT numbers. Never put the customer's VAT number in the
          supplier's field, or the reverse, and do not return null for one just because you found the other.

        LINE ITEMS
        - One entry per printed row in the item table, in order, including struck-out rows.
        - Column headers vary between suppliers. Map them by meaning:
          quantity, unit price, VAT rate, total excluding VAT, total including VAT.
          If a column does not exist, use null.
        - Copy values exactly as printed. Do not calculate or reformat numbers.

        HANDWRITING AND PEN MARKS
        - Full strikeout: a line drawn through the whole row -> IsFullyStruckOut = true.
        - Partial strikeout: a single value crossed out, often with a new value written by
          hand next to it -> add a Correction with the column name, the original printed
          value, and the handwritten new value.
        - Report printed values in the line item fields; corrections go ONLY in Corrections.
        - Signature or initials: set HasSignatureNearby / HasSignatureNearStrikeout = true
          only if a signature, initials or a scribbled paraph is right next to that change.
        - Tick marks in the margin are NOT corrections. Ignore them.
        - Printed table borders and ruled lines are NOT strikeouts.

        STAMPS
        - Record every stamp: its printed text, any handwritten PO number or received date
          inside it, and whether it is signed.

        HANDWRITTEN NOTES
        - Record any other handwritten text (amounts, comments) with where it appears.

        If something is unreadable or you are unsure, use null and explain in ModelComments.
        Never invent values.
        """;
}