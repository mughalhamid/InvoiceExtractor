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
        - Address = street, town and postcode lines only. Account numbers are not an address.
        - Decide which block is which ONCE, using the labels, then do not revisit the decision.
        - An invoice normally shows BOTH VAT numbers. Never put the customer's VAT number in the
          supplier's field, or the reverse, and do not return null for one just because you found the other.

        ITEM TABLE (copy it, do not interpret it)
        - Headers: copy every column header of the item table exactly as printed, left to right.
        - Rows: one entry per item row, in order, including struck-out rows.
        - Cells: one value per header, in the SAME order as Headers, copied exactly as printed.
          Use null for an empty cell. Do not calculate, convert or reformat anything.
        - A description that wraps onto a second printed line belongs to the SAME row.
        - Report the PRINTED value in Cells, even if it is crossed out.

        PEN MARKS ON THE TABLE
        - Full strikeout: a line drawn through the whole row -> IsFullyStruckOut = true.
        - Partial strikeout: a single printed value crossed out, usually with a new value written
          by hand next to it -> add a Correction with Column = that column's printed header,
          OriginalValue = the crossed-out printed value, NewValue = the handwritten value (or null).
        - Signature or initials: set HasSignatureNearby / HasSignatureNearStrikeout = true only
          if a signature, initials or a scribbled paraph is right next to that change.
        - Tick marks in the margin are NOT corrections. Ignore them.
        - Printed table borders and ruled lines are NOT strikeouts.

        STAMPS
        - Record every stamp once: its printed text, any handwritten PO number or received date
          inside it, and whether it is signed.

        HANDWRITTEN NOTES
        - Record handwritten text that is NOT part of a correction or stamp (e.g. an amount written
          next to the totals, a comment at the bottom), each one ONCE, with where it appears.
        - Most invoices have 0 to 3 such notes. Never repeat a note.

        If something is unreadable or you are unsure, use null. Never invent values.
        Stop as soon as every field is filled. Do not add commentary.
        """;
}
