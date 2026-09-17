using System.Globalization;
using Microsoft.Extensions.Logging;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Finds the fiscal receipt an invoice PDF prints — the QR, and the verification code, fiscal day and
/// device stated beside it.
/// </summary>
/// <remarks>
/// Shared by the web and desktop download handlers so the two cannot disagree about where an
/// invoice's receipt comes from. They held a copy each, and a copy is how the per-sale case below
/// came to be missing from both.
/// </remarks>
internal static class InvoicePdfFiscalDetail
{
    /// <summary>
    /// The QR payload to print, with <paramref name="invoice"/>'s receipt details filled in for the
    /// block beside it. Null when this invoice has no receipt to show.
    /// </summary>
    /// <remarks>
    /// Four sources, cheapest and most certain first:
    /// <list type="number">
    /// <item><paramref name="requestedQrCode"/> — the caller already holds the receipt.</item>
    /// <item>The projection already applied to the DTO, from <c>DesktopFiscalTransactions</c> by DocNum.</item>
    /// <item>The sale row, for a per-sale invoice — see <see cref="PerSaleInvoiceRegistry.FindReceiptByDocNumAsync"/>.</item>
    /// <item>The fiscal device itself, as a last resort.</item>
    /// </list>
    /// The third existed nowhere before: a till, vending or van sale signs its receipt under the
    /// sale's own external reference, hours before SAP assigns a DocNum, so sources 2 and 4 — both
    /// keyed on the DocNum — ask about a number the receipt was never filed under. Every such invoice
    /// printed with no fiscal block at all, and printed it silently, which is why the same PDF could
    /// come out with the QR one day and without it the next.
    /// </remarks>
    public static async Task<string?> ResolveAsync(
        ApplicationDbContext dbContext,
        IFiscalReceiptReader fiscalReceiptReader,
        InvoiceDto invoice,
        string? requestedQrCode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var qrCode = requestedQrCode;

        if (string.IsNullOrWhiteSpace(qrCode))
        {
            qrCode = invoice.FiscalQrCode;
        }

        if (string.IsNullOrWhiteSpace(qrCode))
        {
            var perSaleReceipt = await PerSaleInvoiceRegistry.FindReceiptByDocNumAsync(
                dbContext, invoice.DocNum, cancellationToken);

            if (perSaleReceipt is not null)
            {
                qrCode = perSaleReceipt.QrCode;
                invoice.FiscalVerificationCode ??= perSaleReceipt.VerificationCode;
                invoice.FiscalDay ??= perSaleReceipt.FiscalDay;
                invoice.FiscalDeviceId ??= perSaleReceipt.DeviceId?.ToString(CultureInfo.InvariantCulture);
                invoice.FiscalReceiptGlobalNo ??= perSaleReceipt.ReceiptGlobalNo;
            }
        }

        if (string.IsNullOrWhiteSpace(qrCode))
        {
            var receipt = await TryLookupAsync();
            qrCode = receipt?.QrCode;

            // The invoice prints the verification code, day and device beside the QR, so a receipt
            // read from the device must fill them too — not just the QR it answered with.
            if (receipt is { IsFiscalised: true })
            {
                invoice.FiscalVerificationCode ??= receipt.VerificationCode;
                invoice.FiscalDeviceId ??= receipt.DeviceId;
                invoice.FiscalDay ??= receipt.FiscalDay;
                invoice.FiscalReceiptGlobalNo ??= receipt.ReceiptGlobalNo;
            }
        }

        WarnIfFiscalisedWithNothingToPrint(invoice, qrCode, logger);

        return qrCode;

        async Task<FiscalReceiptSnapshot?> TryLookupAsync()
        {
            try
            {
                return await fiscalReceiptReader.TryLookupAsync(
                    invoice.DocNum,
                    ReceiptType.FiscalInvoice,
                    logger,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not load the fiscal receipt for invoice {DocNum} while generating PDF",
                    invoice.DocNum);
                return null;
            }
        }
    }

    /// <summary>
    /// Says so when an invoice recorded as fiscalised has no receipt to print.
    /// </summary>
    /// <remarks>
    /// The PDF drops the whole fiscal block when it has neither a QR nor a verification code, which
    /// is right for an invoice that was never fiscalised and wrong for one that was. Dropped without
    /// a word, it read as a layout regression for weeks and was "fixed" three times in the design
    /// while the cause sat here. A tax invoice missing the receipt its customer holds is worth a line
    /// in the log naming the document.
    /// </remarks>
    private static void WarnIfFiscalisedWithNothingToPrint(InvoiceDto invoice, string? qrCode, ILogger logger)
    {
        if (invoice.IsFiscalized != true
            || !string.IsNullOrWhiteSpace(qrCode)
            || !string.IsNullOrWhiteSpace(invoice.FiscalVerificationCode))
        {
            return;
        }

        logger.LogWarning(
            "Invoice {DocNum} (DocEntry {DocEntry}) is recorded as fiscalised but neither a QR code nor a "
                + "verification code could be resolved for it, so its PDF prints with no fiscal block. "
                + "The receipt exists; the number it is filed under is not this invoice's DocNum.",
            invoice.DocNum,
            invoice.DocEntry);
    }
}
