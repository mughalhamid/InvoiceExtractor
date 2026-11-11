namespace InvoiceExtractor.Extraction;

/// <summary>
/// The instructions sent to the model. Treat this as code: change one thing at a
/// time and re-run against the same invoices to see whether results improve.
/// </summary>
public static class Prompts
{
    /// <summary>Pass 1: the whole page, printed content only.</summary>
    public const string ExtractInvoice = """
        You are an expert accounts-payable clerk reading a scanned supplier invoice.
        Copy what is printed into the JSON schema provided. Follow these rules exactly.
        Copy, do not interpret: never calculate, convert, reformat or guess.

        LOGO AND ADDRESS BLOCKS
        - LogoText: the company name shown in the logo or letterhead graphic, as printed.
        - AddressBlocks: every block of name/address lines on the page (letterhead, account box,
          bill-to box, delivery box), once each, top to bottom.
          Label = the printed label of the block, if it has one (e.g. "Account No: SGM008",
          "Bill to", "Deliver to"); null if the block has no label.
          Lines = every printed line of the block, top to bottom, exactly as printed.
          VatNumber = the VAT number printed inside that block, exactly as printed.
        - Do not decide who is the supplier or the customer. Just copy each block.

        ITEM TABLE
        - Headers: copy every column header of the item table exactly as printed, left to right.
        - Rows: one entry per item row, in order, including rows that are crossed out.
        - Cells: one value per header, in the SAME order as Headers, exactly as printed.
          Use null for an empty cell.
        - A description that wraps onto a second printed line belongs to the SAME row: a printed
          line with no quantity, price or total of its own is NOT a row; add its text to the
          description of the row above. Never copy a row's values onto another row.
        - Ignore all pen marks, crossings-out and handwriting in the table: copy the PRINTED value.

        TOTALS AND FOOTER
        - Totals: Subtotal = amount before VAT (may be labelled "Gross", "Net", "Subtotal");
          VatAmount = total VAT; Total = the final amount payable. Copy as printed.
        - VatSummary: the small VAT summary table near the bottom (columns such as "Vat Code",
          "Vat Rate", "Gross Amt.", "Discount", "Vat Amt."), headers and rows copied as printed,
          even if a line or stamp overlaps it. Empty lists if there is no such table.
        - OtherCharges: any other printed charge, deposit or discount in the totals area
          (e.g. "BCRS", "Discount", "Delivery"), label and amount as printed.

        SIGNATURES
        - Signatures: every printed signature box or line that is NOT inside a stamp
          (e.g. "Signature", "Received by", "Delivered by"), once each: its printed label, where
          it is, and whether someone actually signed it (IsSigned). A stamp's own signature
          line belongs to the stamp, not here.

        STAMPS
        - Record every stamp once: its printed text, any handwritten PO number or received date
          inside it, and whether it is signed.

        HANDWRITTEN NOTES
        - Record handwritten text that is NOT a correction in the item table, NOT inside a stamp and
          NOT a signature (e.g. an amount written next to the totals, a comment at the bottom),
          each one ONCE, with where it appears.
        - Look carefully at the area around the totals and between the stamp and the totals:
          a handwritten amount there (often with a currency sign, possibly slanted) is a note.
        - Most invoices have 0 to 3 such notes. Never repeat a note.

        If something is unreadable or you are unsure, use null. Never invent values.
        Stop as soon as every field is filled. Do not add commentary.
        """;

    /// <summary>Pass 2: the stacked ink strips (see Extraction/PenMarkPass.cs).</summary>
    public static string PenMarks(IReadOnlyList<string> headers, IReadOnlyList<string> rowDescriptions) => $"""
        This image is made of horizontal strips cut from a scanned supplier invoice, enlarged and
        stacked top to bottom with white gaps between them. Each strip was cut out because it
        contains pen ink. Some strips are outside the item table (logo, stamp, footer): ignore those.

        The item table's printed column headers, left to right, are:
        {string.Join(" | ", headers)}

        The item rows, numbered:
        {string.Join("\n", rowDescriptions.Select((d, i) => $"{i + 1}. {d}"))}

        List each pen mark that CHANGES an item row:
        - FullStrikeout: a pen line drawn through the whole row (most of its values are crossed).
        - PartialStrikeout: ONE printed value crossed out, usually with a new value written by hand
          next to it. Column = the header of that value's column; OriginalValue = the crossed-out
          printed value exactly as printed; NewValue = the handwritten value, or null if none.
        RowNumber = the row's number from the list above; RowText = its printed description.
        HasSignatureNearby = true only if initials, a signature or a scribbled paraph is right next
        to THAT change.

        NOT changes: tick marks or arrows in the margin, a pen line UNDER a value (underlining),
        printed text, printed table borders and ruled lines, stamps, notes outside the table.
        Most invoices have 0 to 6 changes. List each change ONCE and never repeat a row.
        If no row is changed, return an empty Marks list. No commentary.
        """;
}
