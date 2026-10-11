using System.Globalization;
using System.Text;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.Invoices.Slip;

/// <summary>How one line of a slip is set.</summary>
public enum SlipLineStyle
{
    Normal = 0,
    Bold = 1,

    /// <summary>Bold and double height: the title and the total. Still 32 columns.</summary>
    Tall = 2
}

/// <summary>One printed line: never wider than <see cref="InvoiceSlipLayout.Width"/>.</summary>
public readonly record struct SlipLine(string Text, SlipLineStyle Style = SlipLineStyle.Normal);

/// <summary>
/// A slip as it is drawn: the body down to the total, the QR, then the fiscal details beneath it.
/// </summary>
/// <param name="Body">Everything down to the total.</param>
/// <param name="Footer">The fiscal details and the thank-you, drawn under the QR.</param>
/// <param name="QrPayload">Null when the receipt has no QR; the footer then says nothing about scanning.</param>
public sealed record InvoiceSlip(IReadOnlyList<SlipLine> Body, IReadOnlyList<SlipLine> Footer, string? QrPayload);

/// <summary>
/// An invoice laid out as the till slip a van's printer gives the customer: 58 mm paper, one fixed
/// font, 32 columns.
/// </summary>
/// <remarks>
/// <para>
/// A sale filed with ZIMRA as a 48 mm receipt (<see cref="ReceiptPrintForm.Receipt48"/>) is a receipt,
/// and the customer was handed one at the van or the till. Sending them an A4 tax invoice for it on
/// WhatsApp is sending a different document from the one in their hand. This is that slip again: the
/// van app's <c>ReceiptLayout</c> and <c>FiscalReceiptPrintout</c>, ported line for line — seller
/// block, title, number and date, buyer, items with the amount ending in the last column, the total
/// between two double rules, then the QR with the fiscal details under it.
/// </para>
/// <para>
/// <b>Nothing here computes money that the A4 invoice does not.</b> A line's amount is
/// <see cref="InvoiceLineTax"/>'s VAT-inclusive total, the same figure the A4 prints as "Total Inc";
/// the VAT and the total are SAP's document figures. The two documents for one invoice cannot disagree.
/// </para>
/// <para>
/// <b>Every line fits.</b> A value that will not sit beside its label drops to its own right-aligned
/// line and names break between words, so no line is wider than 32 characters; and everything is
/// ASCII, as it is on the printer.
/// </para>
/// </remarks>
public static class InvoiceSlipLayout
{
    /// <summary>Columns across 58 mm paper in the printer's font A.</summary>
    public const int Width = 32;

    public const string TradingName = "KEFALOS CHEESE PRODUCTS";
    public const string VatNumber = "220140892";
    public const string TinNumber = "2000022395";
    public const string Title = "FISCAL TAX INVOICE";
    public const string Thanks = "Thank you for your business!";

    private static readonly string[] Addresses =
    [
        "Admin: Harare, ZW",
        "Factory: Bhara Bhara Farm, Harare South, ZW"
    ];

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static InvoiceSlip Compose(InvoiceDto invoice, string? fiscalQrCode)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var qr = string.IsNullOrWhiteSpace(fiscalQrCode) ? null : fiscalQrCode.Trim();
        return new InvoiceSlip(Body(invoice), Footer(invoice, qr is not null), qr);
    }

    private static List<SlipLine> Body(InvoiceDto invoice)
    {
        var lines = new List<SlipLine>();

        lines.Add(new SlipLine(Centre(TradingName), SlipLineStyle.Bold));
        foreach (var address in Addresses)
        {
            lines.AddRange(Wrap(address).Select(part => new SlipLine(Centre(part))));
        }

        lines.AddRange(Pair("VAT No", VatNumber));
        lines.AddRange(Pair("TIN No", TinNumber));
        lines.Add(DoubleRule());

        lines.Add(new SlipLine(Centre(Title), SlipLineStyle.Tall));
        lines.AddRange(Pair("Invoice", invoice.DocNum.ToString(Invariant)));

        // The number on the paper slip from the van or the till, so the two can be matched.
        if (Clean(invoice.VanSaleOrderNumber) is { } saleReference)
        {
            lines.AddRange(Pair("Sale ref", saleReference));
        }

        if (FormatDate(invoice.DocDate) is { } date)
        {
            lines.AddRange(Pair("Date", date));
        }

        AppendBuyer(lines, invoice);

        lines.Add(new SlipLine(PairLine("Item", "Amount"), SlipLineStyle.Bold));

        var items = invoice.Lines ?? [];
        var netSum = items.Sum(line => line.LineTotal);
        foreach (var item in items)
        {
            var (_, totalInc) = InvoiceLineTax.Of(item, netSum, invoice.VatSum);

            lines.AddRange(Wrap(Clean(item.ItemDescription) ?? Clean(item.ItemCode) ?? "-").Select(part => new SlipLine(part)));
            lines.AddRange(Pair("  " + Detail(item), Amount(totalInc)));
        }

        lines.Add(Rule());
        lines.AddRange(Pair("Total excl. VAT", Amount(invoice.DocTotal - invoice.VatSum)));

        // Stated alongside the total, never added to it: the total already carries its tax.
        lines.AddRange(Pair("VAT", Amount(invoice.VatSum)));

        lines.Add(DoubleRule());
        lines.AddRange(Pair($"TOTAL {Clean(invoice.DocCurrency) ?? "USD"}", Amount(invoice.DocTotal), SlipLineStyle.Tall));
        lines.Add(DoubleRule());

        return lines;
    }

    /// <summary>
    /// Who the slip is for. A row with nothing in it is left out, and so is the heading when there is
    /// nothing to say — a heading over empty lines reads as a buyer that was lost.
    /// </summary>
    private static void AppendBuyer(List<SlipLine> lines, InvoiceDto invoice)
    {
        var rows = new[]
            {
                Clean(invoice.CardName),
                JoinAddress(invoice.BillToAddress),
                Clean(invoice.CustomerVatNo) is { } vat ? $"VAT No {vat}" : null,
                Clean(invoice.CustomerTinNumber) is { } tin ? $"TIN No {tin}" : null
            }
            .OfType<string>()
            .ToList();

        lines.Add(Rule());
        if (rows.Count == 0)
        {
            return;
        }

        lines.Add(new SlipLine("Buyer", SlipLineStyle.Bold));
        foreach (var row in rows)
        {
            lines.AddRange(Wrap(row).Select(part => new SlipLine(part)));
        }

        lines.Add(Rule());
    }

    /// <summary>
    /// Printed under the QR: the line that says what the code is for, then what a verifier reads off
    /// the paper, then the thank-you.
    /// </summary>
    private static List<SlipLine> Footer(InvoiceDto invoice, bool hasQr)
    {
        var lines = new List<SlipLine>();

        if (hasQr)
        {
            lines.Add(new SlipLine(Centre("Scan to verify with ZIMRA")));
            // The customer is reading this on the phone that would have to do the scanning.
            lines.Add(new SlipLine(Centre("or tap the code to open it")));
        }

        lines.Add(Rule());

        if (DisplayVerificationCode(invoice.FiscalVerificationCode) is { } code)
        {
            lines.Add(new SlipLine(Centre("Verification code")));
            lines.AddRange(Wrap(code).Select(part => new SlipLine(Centre(part), SlipLineStyle.Bold)));
            lines.Add(Blank());
        }

        if (Clean(invoice.FiscalDay) is { } day)
        {
            lines.AddRange(Pair("Fiscal day", day));
        }

        if (invoice.FiscalReceiptGlobalNo is { } receiptNumber)
        {
            lines.AddRange(Pair("Receipt no", receiptNumber.ToString(Invariant)));
        }

        if (Clean(invoice.FiscalDeviceId) is { } device)
        {
            lines.AddRange(Pair("Device ID", device));
        }

        lines.Add(Blank());
        lines.Add(new SlipLine(Centre(Thanks), SlipLineStyle.Bold));

        return lines;
    }

    // ── The layout's own vocabulary ─────────────────────────────────────

    /// <summary>"2 x 7.22" where SAP gave the price the customer paid for one, "Qty 2" where it did not.</summary>
    private static string Detail(InvoiceLineDto line)
    {
        var quantity = line.Quantity.ToString("0.###", Invariant);
        return line.PriceAfterVat != 0m
            ? $"{quantity} x {Amount(line.PriceAfterVat)}"
            : $"Qty {quantity}";
    }

    /// <summary>Two places and no currency sign, as every figure on the printed slip.</summary>
    public static string Amount(decimal amount) => amount.ToString("F2", Invariant);

    /// <summary>Padded on the left only.</summary>
    public static string Centre(string text) => new string(' ', Math.Max(0, (Width - text.Length) / 2)) + text;

    /// <summary>
    /// A label at the left edge and a value ending in the last column. When the two will not fit on
    /// one line with a space between them the value drops to its own line, still right-aligned.
    /// </summary>
    public static IEnumerable<SlipLine> Pair(string label, string value, SlipLineStyle style = SlipLineStyle.Normal)
    {
        label = Ascii(label);
        value = Ascii(value);

        if (label.Length + 1 + value.Length <= Width)
        {
            yield return new SlipLine(PairLine(label, value), style);
            yield break;
        }

        foreach (var part in Wrap(label))
        {
            yield return new SlipLine(part, style);
        }

        foreach (var part in Wrap(value))
        {
            yield return new SlipLine(part.PadLeft(Width), style);
        }
    }

    private static string PairLine(string label, string value) =>
        label + new string(' ', Width - label.Length - value.Length) + value;

    /// <summary>
    /// Text broken between words to fit the width. A single word longer than a line — a code, a
    /// reference — is the only thing ever split mid-way, because it has nowhere else to go.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string? text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in Ascii(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var remaining = word;

            while (remaining.Length > Width)
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                lines.Add(remaining[..Width]);
                remaining = remaining[Width..];
            }

            if (current.Length > 0 && current.Length + 1 + remaining.Length > Width)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(remaining);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static SlipLine Rule() => new(new string('-', Width));

    private static SlipLine DoubleRule() => new(new string('=', Width));

    private static SlipLine Blank() => new(string.Empty);

    /// <summary>
    /// The text in the characters the slip's font has. Accents are dropped from their letters, the
    /// typographic quotes and dashes SAP names sometimes carry become their plain forms, line breaks
    /// become spaces, and anything else outside ASCII prints as "?" rather than as a missing glyph.
    /// </summary>
    public static string Ascii(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var ascii = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            ascii.Append(character switch
            {
                '\r' or '\n' or '\t' or ' ' => ' ',
                '‘' or '’' => '\'',
                '“' or '”' => '"',
                '–' or '—' => '-',
                >= ' ' and <= '~' => character,
                _ => '?'
            });
        }

        return ascii.ToString();
    }

    /// <summary>The device may hand the code back raw or already grouped; it is printed grouped in fours either way.</summary>
    private static string? DisplayVerificationCode(string? code)
    {
        if (Clean(code) is not { } trimmed)
        {
            return null;
        }

        return trimmed.Contains('-') ? trimmed : FiscalReceiptQrComposer.FormatVerificationCode(trimmed);
    }

    /// <summary>Day first, as dates are written in Zimbabwe. SAP keeps no time on an invoice.</summary>
    private static string? FormatDate(string? docDate)
    {
        if (Clean(docDate) is not { } value)
        {
            return null;
        }

        return DateTime.TryParse(value, Invariant, DateTimeStyles.None, out var date)
            ? date.ToString("dd/MM/yyyy", Invariant)
            : value;
    }

    private static string? JoinAddress(string? address)
    {
        if (Clean(address) is not { } value)
        {
            return null;
        }

        var joined = string.Join(", ", value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0));

        return joined.Length == 0 ? null : joined;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
