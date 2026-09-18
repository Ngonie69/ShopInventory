using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Fiscalises a van sale, and only then posts its invoice to SAP.
/// </summary>
/// <remarks>
/// <para><b>Why this order.</b> A van sale's receipt is the customer's copy of the sale and ZIMRA's record of
/// it; the SAP invoice is the business's. Posting first and fiscalising in the background — what this
/// replaced — left every online van sale printed without a verification code, and let a sale exist in the
/// books with nothing declared behind it if the background work was lost, which it was on every restart.
/// Signing first means no invoice reaches SAP without a receipt already standing behind it.</para>
///
/// <para><b>What that costs, and how it is paid.</b> A receipt cannot be withdrawn, so once one exists the
/// sale stands whatever SAP then says. Three things keep that from turning into a receipt for a sale the
/// books cannot take:</para>
/// <list type="number">
/// <item>Nothing is signed unless its reservation is <see cref="ReservationStatus.Pending"/> — stock was
/// checked and is held. A sale the van cannot supply is refused before the device is ever asked.</item>
/// <item>Each line's VAT group is decided from the item master before signing and written onto the
/// reservation line, so the rate the receipt declared is the rate SAP charges. Left to SAP it would pick
/// after the receipt was already signed.</item>
/// <item>A SAP refusal after signing is <see cref="VanSaleFiscalFirstStatus.AwaitingSap"/>, not a failure:
/// the reservation is kept holding the stock — reopened if SAP's refusal closed it — and the post is
/// retried. The fiscalisation never is.</item>
/// </list>
///
/// <para><b>The row.</b> The receipt lives on a <see cref="DesktopSaleEntity"/> under
/// <see cref="SaleSourceSystems.VanSalesOnline"/>, written <i>before</i> the device is called so a process
/// that dies mid-call leaves something to find the receipt by. It is marked Consolidated from the start
/// because the invoice is the reservation's to produce, never this row's — see the same rule in
/// <c>CreateVanSalesDirectInvoiceHandler.BuildReceiptRow</c>. Once posted it carries the SAP DocNum, which is
/// how <c>CreditNoteOriginalReceipt</c> finds the receipt number a credit note must cite.</para>
///
/// <para>The receipt is filed under the reservation's external reference — the van order — through
/// <see cref="IFiscalizationService.FiscalizePreSapInvoiceAsync"/>, the same call the till uses. That
/// reference is also the invoice's <c>U_Van_saleorder</c>, so the receipt and the invoice name each other.</para>
/// </remarks>
public sealed class VanSaleFiscalFirstPoster(
    ApplicationDbContext db,
    IStockReservationService reservations,
    DesktopSaleFiscaliser fiscaliser,
    DesktopCreditSapPoster creditPoster,
    IOptions<TaxSettings> tax,
    ILogger<VanSaleFiscalFirstPoster> logger)
{
    /// <summary>
    /// How long a fiscalised sale's reservation is held ahead of each post. Long enough to outlast a SAP
    /// outage between retries; the queue settling the sale is what finally releases it.
    /// </summary>
    internal static readonly TimeSpan PostingHold = TimeSpan.FromMinutes(60);

    public async Task<VanSaleFiscalFirstOutcome> FiscaliseThenPostAsync(
        VanSaleFiscalFirstRequest request,
        CancellationToken cancellationToken)
    {
        var reservation = await db.StockReservations
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.ReservationId == request.ReservationId, cancellationToken);

        if (reservation is null)
        {
            return NotPostable(null, $"Reservation {request.ReservationId} does not exist.");
        }

        var reference = reservation.ExternalReferenceId?.Trim();

        if (string.IsNullOrEmpty(reference))
        {
            // The reference is the receipt's permanent number. Without one there is nothing stable to file
            // it under, and nothing a retry could find it by.
            return NotPostable(null, $"Reservation {request.ReservationId} carries no van order reference.");
        }

        var sale = await db.DesktopSales
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.ExternalReferenceId == reference, cancellationToken);

        if (sale is not null &&
            !string.Equals(sale.SourceSystem, SaleSourceSystems.VanSalesOnline, StringComparison.Ordinal))
        {
            return NotPostable(
                null,
                $"Reference {reference} already belongs to a {sale.SourceSystem} sale, so it cannot also be " +
                "this invoice's receipt.");
        }

        var createdNow = false;

        if (sale is null)
        {
            if (!string.Equals(reservation.Status, ReservationStatus.Pending, StringComparison.Ordinal))
            {
                return NotPostable(
                    null,
                    $"Reservation {request.ReservationId} is {reservation.Status}, so its stock is not held. " +
                    "Nothing was signed.");
            }

            if (reservation.Lines.Count == 0)
            {
                return NotPostable(null, $"Reservation {request.ReservationId} has no lines to sign.");
            }

            sale = await BuildSaleAsync(reservation, reference, request, cancellationToken);
            db.DesktopSales.Add(sale);
            createdNow = true;
        }

        AlignReservationTaxCodes(reservation, sale);

        // Before the device is called, and deliberately. Nothing irrevocable has happened yet, so the request's
        // own token still governs; after the call nothing may.
        await db.SaveChangesAsync(cancellationToken);

        if (sale.FiscalizationStatus != DesktopSaleFiscalizationStatus.Success)
        {
            var unsigned = await FiscaliseAsync(sale, createdNow, request, cancellationToken);

            if (unsigned is not null)
            {
                return unsigned;
            }
        }

        // A receipt exists. Past this line the sale stands, and no caller going away may stop the record
        // of it: the rest runs on CancellationToken.None, for the reason PersistSignedReceiptAsync gives.
        return await PostAsync(reservation, sale, request);
    }

    private async Task<VanSaleFiscalFirstOutcome?> FiscaliseAsync(
        DesktopSaleEntity sale,
        bool createdNow,
        VanSaleFiscalFirstRequest request,
        CancellationToken cancellationToken)
    {
        if (sale.FiscalizationRequiresReconciliation)
        {
            // The device could not say whether it signed. Asking again automatically is how a second receipt
            // is made, so it waits for a person — exactly as DesktopSaleFiscalisationSweep treats a till sale.
            return new VanSaleFiscalFirstOutcome(
                VanSaleFiscalFirstStatus.FiscalUnresolved,
                sale,
                Error: sale.FiscalError ?? "The fiscal device could not confirm whether this sale was signed.");
        }

        // A row that was already here may have reached the device on an attempt that never saved its answer.
        var isRetry = !createdNow || request.MayAlreadyBeFiscalised;

        try
        {
            await fiscaliser.FiscaliseAsync(sale, cancellationToken, isRetry);
        }
        catch (Exception ex) when (isRetry && ex is not OperationCanceledException)
        {
            // Only the lookup throws out of the fiscaliser, and it throws before touching the row. The device
            // could not be asked, so nothing was sent and the sale is exactly as it was.
            logger.LogWarning(
                ex,
                "Could not ask the fiscal device whether van sale {Reference} is already signed; nothing was sent.",
                sale.ExternalReferenceId);

            return new VanSaleFiscalFirstOutcome(
                VanSaleFiscalFirstStatus.FiscalUnchecked,
                sale,
                Error: $"The fiscal device could not be reached to check this sale: {ex.Message}",
                Transient: true);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return sale.FiscalizationStatus switch
        {
            DesktopSaleFiscalizationStatus.Success => null,

            _ when sale.FiscalizationRequiresReconciliation => new VanSaleFiscalFirstOutcome(
                VanSaleFiscalFirstStatus.FiscalUnresolved,
                sale,
                Error: sale.FiscalError ?? "The fiscal device could not confirm whether this sale was signed."),

            // Fiscalisation switched off is not a licence to invoice without a receipt. The till refuses to
            // post a Skipped sale for the same reason.
            DesktopSaleFiscalizationStatus.Skipped => new VanSaleFiscalFirstOutcome(
                VanSaleFiscalFirstStatus.FiscalFailed,
                sale,
                Error: "Fiscalisation is switched off, and a van sale is not invoiced without a receipt."),

            _ => new VanSaleFiscalFirstOutcome(
                VanSaleFiscalFirstStatus.FiscalFailed,
                sale,
                Error: sale.FiscalError ?? "The fiscal device refused the receipt.",
                Transient: SapFailureClassifier.ContainsAvailabilitySignal(sale.FiscalError))
        };
    }

    private async Task<VanSaleFiscalFirstOutcome> PostAsync(
        StockReservationEntity reservation,
        DesktopSaleEntity sale,
        VanSaleFiscalFirstRequest request)
    {
        var persist = CancellationToken.None;

        if (string.Equals(reservation.Status, ReservationStatus.Cancelled, StringComparison.Ordinal))
        {
            // Cancelled is somebody's decision, not SAP's, so it is not undone here. The receipt still
            // stands, which is exactly why a person has to see it.
            return await AwaitSapAsync(
                sale,
                $"Reservation {reservation.ReservationId} was cancelled after this sale was fiscalised. The " +
                "receipt stands; the invoice needs raising by hand or the receipt crediting.",
                transient: false);
        }

        await HoldForPostingAsync(reservation);

        ConfirmReservationResponseDto confirmed;

        try
        {
            confirmed = await reservations.ConfirmReservationAsync(
                new ConfirmReservationRequest
                {
                    ReservationId = reservation.ReservationId,
                    DocDate = request.DocDate,
                    DocDueDate = request.DocDueDate,
                    NumAtCard = request.NumAtCard,
                    Comments = request.Comments,
                    // Already signed. Asking the confirm to fiscalise as well would file a second receipt for
                    // the same sale, under its DocNum.
                    Fiscalize = false
                },
                persist);
        }
        catch (Exception ex)
        {
            await db.Entry(reservation).ReloadAsync(persist);
            await HoldForPostingAsync(reservation);

            return await AwaitSapAsync(sale, ex.Message, SapFailureClassifier.IsTransient(ex));
        }

        if (confirmed.Success && confirmed.SAPDocNum.HasValue)
        {
            sale.SapDocEntry = confirmed.SAPDocEntry;
            sale.SapDocNum = confirmed.SAPDocNum;
            sale.PostedAt ??= DateTime.UtcNow;
            sale.LastPostingError = null;
            await db.SaveChangesAsync(persist);

            await RaiseDeferredCreditsAsync(sale);

            return new VanSaleFiscalFirstOutcome(
                VanSaleFiscalFirstStatus.Posted,
                sale,
                confirmed.SAPDocEntry,
                confirmed.SAPDocNum);
        }

        // SAP refused, or another request holds the post. The confirm marks a non-transient refusal Failed,
        // which would drop the hold on stock that has already left on a receipt — so it is reopened.
        await db.Entry(reservation).ReloadAsync(persist);
        await HoldForPostingAsync(reservation);

        var error = string.Join(
            " ",
            new[] { confirmed.Message }.Concat(confirmed.Errors ?? []).Where(m => !string.IsNullOrWhiteSpace(m)));

        return await AwaitSapAsync(
            sale,
            string.IsNullOrWhiteSpace(error) ? "SAP did not accept the invoice." : error,
            transient: SapFailureClassifier.ContainsAvailabilitySignal(error)
                       || (confirmed.Errors ?? []).Any(SapFailureClassifier.ContainsAvailabilitySignal)
                       || string.Equals(reservation.Status, ReservationStatus.Confirming, StringComparison.Ordinal));
    }

    /// <summary>
    /// Raises the SAP credit memos for fiscal credits taken against this sale before it posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A van sale is signed before SAP is asked, so there is a window — minutes when SAP refuses and
    /// hours when it is down — in which the receipt exists and the invoice does not. A return handed
    /// back in that window is credited with ZIMRA and its memo deferred, because there is no invoice
    /// to raise one against yet; this is the hook that raises it the moment there is. The same hook
    /// <c>VanSalesEndOfDayPostingService</c> has for the offline route, and
    /// <c>PostQueuedVanInvoicesHandler</c> for a sale the queue posts later.
    /// </para>
    /// <para>
    /// Advisory, and never allowed to fail the post. The invoice is in SAP by the time this runs, and
    /// the caller reads a failure as "not posted" — which would put a second invoice in SAP for one
    /// ZIMRA receipt. A credit left owing stays on its own row for <c>DesktopCreditSapSweep</c>.
    /// </para>
    /// </remarks>
    private async Task RaiseDeferredCreditsAsync(DesktopSaleEntity sale)
    {
        try
        {
            await creditPoster.SettleForSaleAsync(sale.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Van sale {Reference} posted as invoice {DocNum}, but the fiscal credits against it "
                + "could not be raised in SAP.",
                sale.ExternalReferenceId,
                sale.SapDocNum);
        }
    }

    private async Task<VanSaleFiscalFirstOutcome> AwaitSapAsync(
        DesktopSaleEntity sale,
        string error,
        bool transient)
    {
        sale.PostingAttempts++;
        sale.LastPostingError = error.Length > 2000 ? error[..2000] : error;
        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogWarning(
            "Van sale {Reference} is fiscalised (receipt {Receipt}) but SAP has not taken its invoice: {Error}",
            sale.ExternalReferenceId,
            sale.FiscalReceiptNumber,
            error);

        return new VanSaleFiscalFirstOutcome(
            VanSaleFiscalFirstStatus.AwaitingSap,
            sale,
            Error: error,
            Transient: transient);
    }

    /// <summary>
    /// Keeps a fiscalised sale's stock held until SAP takes the invoice.
    /// </summary>
    /// <remarks>
    /// The goods have left under a receipt, so a lapsed hold would put them back on the shelf for a till to
    /// sell a second time. Failed and Expired are the confirm's and the clock's words for "this reservation is
    /// over", and neither is true of a sale that already happened.
    /// </remarks>
    private async Task HoldForPostingAsync(StockReservationEntity reservation)
    {
        var now = DateTime.UtcNow;
        var changed = false;

        if (reservation.Status is ReservationStatus.Failed or ReservationStatus.Expired)
        {
            logger.LogWarning(
                "Reopening reservation {ReservationId} ({Status}) because its sale is already fiscalised.",
                reservation.ReservationId,
                reservation.Status);

            reservation.Status = ReservationStatus.Pending;
            changed = true;
        }

        if (string.Equals(reservation.Status, ReservationStatus.Pending, StringComparison.Ordinal) &&
            reservation.ExpiresAt < now + PostingHold)
        {
            reservation.ExpiresAt = now + PostingHold;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Writes the receipt's tax codes onto the reservation, so SAP charges what the receipt declared.
    /// </summary>
    private static void AlignReservationTaxCodes(StockReservationEntity reservation, DesktopSaleEntity sale)
    {
        var codes = sale.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.TaxCode))
            .GroupBy(line => line.LineNum)
            .ToDictionary(group => group.Key, group => group.First().TaxCode);

        foreach (var line in reservation.Lines)
        {
            if (codes.TryGetValue(line.LineNum, out var code) &&
                !string.Equals(line.TaxCode, code, StringComparison.OrdinalIgnoreCase))
            {
                line.TaxCode = code;
            }
        }
    }

    private async Task<DesktopSaleEntity> BuildSaleAsync(
        StockReservationEntity reservation,
        string reference,
        VanSaleFiscalFirstRequest request,
        CancellationToken cancellationToken)
    {
        var vatGroups = await ItemVatGroups.ResolveAsync(
            db, reservation.Lines.Select(line => line.ItemCode), logger, cancellationToken);

        var lines = reservation.Lines
            .OrderBy(line => line.LineNum)
            .Select(line =>
            {
                var taxCode = ItemVatGroups.TaxCodeFor(line.ItemCode, line.TaxCode, vatGroups);
                var effectivePrice = line.UnitPrice * (1 - line.DiscountPercent / 100m);

                // Net, as SAP takes it: the invoice posts UnitPrice before tax and applies the line's own
                // code. DesktopSaleFiscaliser grosses it up for the receipt at that same code's rate.
                return new DesktopSaleLineEntity
                {
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.OriginalQuantity,
                    UnitPrice = line.UnitPrice,
                    LineTotal = Math.Round(
                        line.OriginalQuantity * effectivePrice, 2, MidpointRounding.AwayFromZero),
                    WarehouseCode = line.WarehouseCode,
                    CostCentreCode = line.CostCentreCode,
                    TaxCode = taxCode,
                    TaxPercent = tax.Value.RateFor(taxCode) * 100m,
                    DiscountPercent = line.DiscountPercent,
                    UoMCode = line.UoMCode
                };
            })
            .ToList();

        var subtotal = lines.Sum(line => line.LineTotal);
        var vat = tax.Value.VatOnBasket(lines.Select(line => (line.LineTotal, line.TaxCode)));

        return new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSalesOnline,
            CardCode = reservation.CardCode,
            // Who bought. On a van sale CardCode is the van's own account and the same on every sale it makes.
            CardName = reservation.RouteCustomerName ?? reservation.CardName,
            RouteCustomerId = reservation.RouteCustomerId,
            RouteCustomerCode = reservation.RouteCustomerCode,
            RouteCustomerName = reservation.RouteCustomerName,
            DocDate = ParseDocDate(request.DocDate) ?? AuditService.ToCAT(DateTime.UtcNow).Date,
            NumAtCard = request.NumAtCard ?? reference,
            Comments = request.Comments,
            TotalAmount = subtotal + vat,
            VatAmount = vat,
            Currency = string.IsNullOrWhiteSpace(reservation.Currency) ? "USD" : reservation.Currency,
            WarehouseCode = lines.Select(line => line.WarehouseCode).FirstOrDefault(code => !string.IsNullOrEmpty(code))
                            ?? string.Empty,
            CostCentreCode = lines.Select(line => line.CostCentreCode).FirstOrDefault(code => !string.IsNullOrEmpty(code)),
            PaymentMethod = reservation.PaymentMethod,
            AmountPaid = Math.Max(0m, request.AmountPaid),

            // The invoice is the reservation's to produce. Consolidated is what keeps every posting job from
            // offering this row to SAP a second time.
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Pending,

            // Signed by the server's device, not a handset's: there is no handset receipt to hand on.
            ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.NotApplicable,

            CreatedBy = reservation.CreatedBy,
            CreatedAt = DateTime.UtcNow,
            Lines = lines
        };
    }

    private static DateTime? ParseDocDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Date
            : null;

    private static VanSaleFiscalFirstOutcome NotPostable(DesktopSaleEntity? sale, string error) =>
        new(VanSaleFiscalFirstStatus.NotPostable, sale, Error: error);
}
