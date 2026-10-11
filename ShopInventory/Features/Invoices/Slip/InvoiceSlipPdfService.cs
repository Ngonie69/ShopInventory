using iText.Barcodes;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Slip;

public interface IInvoiceSlipPdfService
{
    /// <summary>Renders the invoice as a till slip: one narrow page as long as the slip is.</summary>
    byte[] GenerateSlipPdf(InvoiceDto invoice, string? fiscalQrCode);
}

/// <summary>
/// Draws <see cref="InvoiceSlipLayout"/>'s slip as a PDF: the logo, the body, the QR, the fiscal details.
/// </summary>
/// <remarks>
/// <para>
/// One page, the proportions of 58 mm roll paper and exactly as tall as the slip, so a phone shows it
/// edge to edge the way the paper looks in the hand and nothing is lost to a page break. It is drawn
/// a little larger than the paper — the same 32 columns at 9 pt rather than the printer's 7 — so it
/// also reads at full size on a desktop.
/// </para>
/// <para>
/// Every line is set in Courier at a fixed advance, so a column on the page is a column in the
/// layout and the figures end where the printer ends them. Double height is the printer's too: twice
/// the size, squeezed to half its width, so a tall line is still 32 columns. Courier is one of the
/// fonts every PDF reader carries, which keeps a slip to a few kilobytes beyond its logo.
/// </para>
/// </remarks>
public sealed class InvoiceSlipPdfService(ILogger<InvoiceSlipPdfService> logger) : IInvoiceSlipPdfService
{
    private const float FontSize = 9f;

    /// <summary>Courier's advance is 600 units in every glyph.</summary>
    private const float ColumnWidth = FontSize * 0.6f;

    private const float LineHeight = 11.5f;
    private const float TallLineHeight = 2 * LineHeight - 2f;
    private const float SideMargin = 16f;
    private const float TopMargin = 16f;
    private const float BottomMargin = 20f;
    private const float LogoWidth = 104f;
    private const float LogoGap = 8f;
    private const float QrSize = 104f;
    private const float QrGap = 8f;

    private static readonly float TextWidth = InvoiceSlipLayout.Width * ColumnWidth;
    private static readonly float PageWidth = TextWidth + 2 * SideMargin;

    private static readonly Lazy<ImageData?> Logo = new(LoadLogo);

    public byte[] GenerateSlipPdf(InvoiceDto invoice, string? fiscalQrCode)
    {
        var slip = InvoiceSlipLayout.Compose(invoice, fiscalQrCode);
        var logo = Logo.Value;
        var logoHeight = logo is null ? 0f : LogoWidth * logo.GetHeight() / logo.GetWidth();

        var pageHeight = TopMargin
            + (logo is null ? 0f : logoHeight + LogoGap)
            + Height(slip.Body)
            + (slip.QrPayload is null ? 0f : QrGap + QrSize + QrGap)
            + Height(slip.Footer)
            + BottomMargin;

        using var stream = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(stream)))
        {
            pdf.GetDocumentInfo().SetTitle($"Kefalos fiscal tax invoice {invoice.DocNum}");

            var page = pdf.AddNewPage(new PageSize(PageWidth, pageHeight));
            var canvas = new PdfCanvas(page);
            var regular = PdfFontFactory.CreateFont(StandardFonts.COURIER);
            var bold = PdfFontFactory.CreateFont(StandardFonts.COURIER_BOLD);
            var top = pageHeight - TopMargin;

            if (logo is not null)
            {
                canvas.AddImageFittedIntoRectangle(
                    logo,
                    new Rectangle((PageWidth - LogoWidth) / 2, top - logoHeight, LogoWidth, logoHeight),
                    false);
                top -= logoHeight + LogoGap;
            }

            top = Draw(canvas, slip.Body, top, regular, bold);

            if (slip.QrPayload is { } qrPayload)
            {
                top -= QrGap;
                DrawQr(pdf, page, canvas, qrPayload, new Rectangle((PageWidth - QrSize) / 2, top - QrSize, QrSize, QrSize), invoice.DocEntry);
                top -= QrSize + QrGap;
            }

            Draw(canvas, slip.Footer, top, regular, bold);
            canvas.Release();
        }

        return stream.ToArray();
    }

    private static float Height(IReadOnlyList<SlipLine> lines) =>
        lines.Sum(line => line.Style == SlipLineStyle.Tall ? TallLineHeight : LineHeight);

    /// <summary>Sets the lines downward from <paramref name="top"/> and returns where they ended.</summary>
    private static float Draw(PdfCanvas canvas, IReadOnlyList<SlipLine> lines, float top, PdfFont regular, PdfFont bold)
    {
        foreach (var line in lines)
        {
            var tall = line.Style == SlipLineStyle.Tall;
            var height = tall ? TallLineHeight : LineHeight;

            if (line.Text.Length > 0)
            {
                var size = tall ? FontSize * 2 : FontSize;

                canvas.BeginText()
                    .SetFontAndSize(line.Style == SlipLineStyle.Normal ? regular : bold, size)
                    // Double height only: twice the size at half the width keeps the 32 columns.
                    .SetHorizontalScaling(tall ? 50f : 100f)
                    .MoveText(SideMargin, top - height + (tall ? 5.5f : 3f))
                    .ShowText(line.Text)
                    .EndText();
            }

            top -= height;
        }

        return top;
    }

    /// <summary>
    /// The QR, and a link over it to the same address: the customer reading this on their phone cannot
    /// scan their own screen, but they can tap it.
    /// </summary>
    private void DrawQr(PdfDocument pdf, PdfPage page, PdfCanvas canvas, string payload, Rectangle box, int docEntry)
    {
        try
        {
            var code = new BarcodeQRCode(payload).CreateFormXObject(ColorConstants.BLACK, pdf);
            canvas.AddXObjectFittedIntoRectangle(code, box);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not draw the fiscal QR code on the slip for invoice {DocEntry}", docEntry);
            return;
        }

        if (Uri.TryCreate(payload, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            page.AddAnnotation(new PdfLinkAnnotation(box)
                .SetAction(PdfAction.CreateURI(uri.AbsoluteUri))
                .SetBorder(new PdfArray(new float[] { 0, 0, 0 })));
        }
    }

    private static ImageData? LoadLogo()
    {
        var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "kefalos-logo.jpg");
        try
        {
            return File.Exists(path) ? ImageDataFactory.Create(path) : null;
        }
        catch
        {
            // A slip without its letterhead is still the customer's receipt.
            return null;
        }
    }
}
