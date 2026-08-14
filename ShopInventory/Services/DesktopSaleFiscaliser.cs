using System.Globalization;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Hands one till or vending sale to the fiscalisation platform and records what came back.
/// </summary>
/// <remarks>
/// Shared by the two routes that need it, because they must behave identically: a shop till fiscalises
/// inline so the receipt can print before the customer leaves, and vending fiscalises in the
/// background because nothing is printed and the counter is not waiting. Same call, same result
/// mapping, same refusal to retry what must not be retried — only the timing differs.
///
/// The caller owns the transaction. This mutates the sale and leaves saving to whoever called it, so
/// the inline path can save once alongside the sale itself and the sweep can save per sale.
/// </remarks>
public sealed class DesktopSaleFiscaliser(
    IFiscalizationService fiscalizationService,
    INotificationService notificationService,
    IOptions<TaxSettings> tax,
    ILogger<DesktopSaleFiscaliser> logger)
{
    /// <summary>
    /// Fiscalises the sale, moving it out of <see cref="DesktopSaleFiscalizationStatus.Pending"/>.
    /// </summary>
    /// <param name="sale">The sale to sign. Mutated in place; the caller saves.</param>
    /// <param name="cancellationToken">Cancellation for the platform call.</param>
    /// <param name="isRetry">
    /// Whether an earlier attempt may already have reached the platform. When true the platform is
    /// asked for an existing receipt first, and one that is found is adopted rather than signed again.
    /// </param>
    public async Task FiscaliseAsync(
        DesktopSaleEntity sale,
        CancellationToken cancellationToken,
        bool isRetry = false)
    {
        // Outside the try, deliberately. A previous attempt was made and we never learned its outcome
        // — the process died, the save failed, the request was abandoned — so submitting again could
        // sign a second receipt for one sale, which cannot be withdrawn. If the platform cannot be
        // asked, that has to reach the caller as a failure to CHECK rather than be recorded as a
        // failure to fiscalise: the sale must be left exactly as it was, not marked Failed and not
        // charged an attempt, so the next pass tries the question again.
        if (isRetry)
        {
            var existing = await fiscalizationService.FindPreSapReceiptAsync(
                sale.ExternalReferenceId!, cancellationToken);

            if (existing is not null)
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Success;
                sale.FiscalReceiptNumber = existing.ReceiptGlobalNo;
                sale.FiscalDayNo = existing.FiscalDayNo;
                sale.FiscalError = null;
                return;
            }
        }

        sale.FiscalizationAttempts++;

        try
        {
            // DocNum/DocEntry are deliberately left unset. The sale has no SAP document yet, and the
            // local primary key is not a SAP identifier: passing it as a DocEntry would make the
            // platform look up — and fiscalise — whichever unrelated SAP invoice holds that number.
            var invoiceDto = new InvoiceDto
            {
                DocDate = sale.DocDate.ToString("yyyy-MM-dd"),
                CardCode = sale.CardCode,
                CardName = sale.CardName,
                DocTotal = sale.TotalAmount,
                VatSum = sale.VatAmount,
                DocCurrency = sale.Currency,
                Comments = sale.Comments,
                Lines = sale.Lines.Select(l =>
                {
                    // The receipt is submitted TaxInclusive, and the platform reads GrossPrice in
                    // preference to UnitPrice. A sale's UnitPrice is net and carries a separate
                    // discount, so sending it raw declared a price that was neither discounted nor
                    // taxed: the customer's VAT was understated on every receipt, and a discounted
                    // line was declared at its undiscounted price.
                    var effectivePrice = l.UnitPrice * (1 - l.DiscountPercent / 100m);
                    var rate = tax.Value.RateFor(l.TaxCode);

                    return new InvoiceLineDto
                    {
                        LineNum = l.LineNum,
                        ItemCode = l.ItemCode,
                        ItemDescription = l.ItemDescription,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                        GrossPrice = Math.Round(effectivePrice * (1 + rate), 2, MidpointRounding.AwayFromZero),
                        LineTotal = l.LineTotal,
                        WarehouseCode = l.WarehouseCode,
                        // Without this every line fell through to the standard-rated default tax id,
                        // so a zero-rated item was declared to ZIMRA as standard-rated.
                        TaxCode = l.TaxCode,
                        DiscountPercent = l.DiscountPercent
                    };
                }).ToList()
            };

            // The external reference is the invoice number: unique, traceable, and stable across
            // retries, which is what the platform's idempotency key requires. Regenerating it on a
            // retry would sign the same sale twice under two numbers.
            var result = await fiscalizationService.FiscalizePreSapInvoiceAsync(
                invoiceDto,
                sale.ExternalReferenceId!,
                paymentType: TenderTypes.ToMoneyType(sale.PaymentMethod),
                cancellationToken: cancellationToken);

            if (result.Success && !result.Skipped)
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Success;
                sale.FiscalReceiptNumber = result.ReceiptGlobalNo;
                sale.FiscalDeviceNumber = result.DeviceSerial;
                sale.FiscalQRCode = result.QRCode;
                sale.FiscalVerificationCode = result.VerificationCode;
                sale.FiscalDayNo = result.FiscalDayNo;
                sale.FiscalError = null;
            }
            else if (result.Skipped)
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Skipped;
            }
            else
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed;
                sale.FiscalError = result.Message ?? result.ErrorDetails;

                // The platform could not tell us whether the receipt was signed. Retrying could sign
                // it a second time, and a duplicate fiscal receipt cannot be withdrawn — so this sale
                // stops here and waits for someone to look it up at FDMS.
                if (result.RequiresReconciliation)
                {
                    sale.FiscalizationRequiresReconciliation = true;

                    logger.LogError(
                        "Fiscalisation of desktop sale {ExternalReference} needs reconciliation at FDMS and will "
                        + "not be retried: {Message}",
                        sale.ExternalReferenceId,
                        sale.FiscalError);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fiscalization failed for desktop sale {SaleId}", sale.Id);
            sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed;
            sale.FiscalError = ex.Message;
        }

        if (sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed)
        {
            await NotifyFailedAsync(sale, cancellationToken);
        }
    }

    /// <summary>
    /// A till sale that did not fiscalise needs someone to act on it, and would otherwise exist only
    /// in the log and in the row's FiscalError.
    /// </summary>
    /// <remarks>
    /// Only the failures are notified. The sales themselves are far too frequent for the bell — they
    /// already reach the Web as a live "DesktopSaleCreated" hub event, which is the right shape for a
    /// feed.
    /// </remarks>
    private async Task NotifyFailedAsync(DesktopSaleEntity sale, CancellationToken cancellationToken)
    {
        try
        {
            var reconciliationNote = sale.FiscalizationRequiresReconciliation
                ? " It will not be retried automatically: check FDMS for an existing receipt before acting."
                : string.Empty;

            await notificationService.CreateNotificationAsync(
                ModuleNotificationFactory.CreateBroadcastNotification(
                    $"Desktop Sale Not Fiscalized: {sale.ExternalReferenceId}",
                    $"Sale {sale.ExternalReferenceId} for " +
                    $"{ModuleNotificationFactory.DescribeBusinessPartner(sale.CardCode, sale.CardName)} " +
                    $"totaling {ModuleNotificationFactory.DescribeMoney(sale.Currency, sale.TotalAmount)} " +
                    $"could not be fiscalized: {sale.FiscalError ?? "no detail was returned"}.{reconciliationNote}",
                    "Error",
                    "SalesOrder",
                    "DesktopSale",
                    sale.Id.ToString(),
                    "/desktop-sales",
                    new Dictionary<string, string>
                    {
                        ["saleId"] = sale.Id.ToString(),
                        ["externalReferenceId"] = sale.ExternalReferenceId ?? string.Empty,
                        ["cardCode"] = sale.CardCode ?? string.Empty,
                        ["cardName"] = sale.CardName ?? string.Empty,
                        ["currency"] = sale.Currency ?? string.Empty,
                        ["totalAmount"] = sale.TotalAmount.ToString(CultureInfo.InvariantCulture),
                        ["fiscalError"] = sale.FiscalError ?? string.Empty,
                        ["requiresReconciliation"] = sale.FiscalizationRequiresReconciliation
                            ? bool.TrueString
                            : bool.FalseString
                    }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Failed to publish fiscalization failure notification for desktop sale {SaleId}", sale.Id);
        }
    }
}
