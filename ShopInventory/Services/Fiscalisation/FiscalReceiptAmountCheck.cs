using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Compares a signed sale's SAP invoice with the receipt ZIMRA holds for it, the moment SAP has the invoice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A van sale is signed before SAP is asked, from prices this server worked out, and
/// SAP then prices the same lines itself. The two used to be compared only when somebody read the
/// fiscalisation console's reconciliation. SAP invoice 784863 (VAN005-INV-20261005-A64C0F) was charged 8.70
/// while its receipt declared 7.50, because <c>InvoicePostingJob</c> filed net prices as gross. Every
/// converted van order signed that way was understated, and nothing said so when it happened.
/// </para>
/// <para>
/// <b>What counts as agreeing.</b> Within a cent of SAP's total, or within a cent of SAP's lines re-priced
/// the way a receipt prices them — each unit at its tax-inclusive price rounded to the cent. That second
/// figure is the reconciliation's own "till rounding" rule, so a sale this passes is one the reconciliation
/// will not flag either.
/// </para>
/// <para>
/// <b>Advisory only.</b> The invoice is in SAP and the receipt is with ZIMRA by the time this runs, and
/// neither can be withdrawn, so a mismatch is recorded for a person. It is never thrown: the caller reads a
/// failure as "not posted", which would offer SAP a second invoice for one receipt. A receipt the platform
/// does not hold yet — a handset's, still on its way — is not compared; the reconciliation sees it later.
/// </para>
/// </remarks>
public sealed class FiscalReceiptAmountCheck(
    ApplicationDbContext db,
    IFiscalizationService fiscalization,
    ILogger<FiscalReceiptAmountCheck> logger)
{
    /// <summary>The reconciliation's own tolerance: anything inside a cent is not a finding.</summary>
    internal const decimal Tolerance = 0.01m;

    /// <summary>
    /// Compares the posted invoice with the receipt filed under <paramref name="reference"/>, and raises an
    /// Exception Center incident when they disagree. Never throws.
    /// </summary>
    public async Task CheckAsync(string? reference, InvoiceDto? sapInvoice)
    {
        if (string.IsNullOrWhiteSpace(reference) || sapInvoice is null)
        {
            return;
        }

        try
        {
            // The caller's token is not used: the invoice is posted, and the comparison is owed whether or
            // not the request that posted it is still waiting.
            var receipt = await fiscalization.GetArchivedPreSapReceiptAsync(reference, CancellationToken.None);

            if (receipt is null)
            {
                logger.LogInformation(
                    "Not comparing invoice {DocNum} with its receipt: the platform holds no receipt for {Reference} yet.",
                    sapInvoice.DocNum,
                    reference);
                return;
            }

            var receiptTotal = receipt.ReceiptTotal;
            var repriced = RepricedAtReceiptPrices(sapInvoice);

            if (Agrees(receiptTotal, sapInvoice.DocTotal, repriced))
            {
                return;
            }

            await RaiseAsync(reference, sapInvoice, receipt, receiptTotal, repriced);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not compare SAP invoice {DocNum} with the receipt for {Reference}. The reconciliation is now "
                + "the only thing that will.",
                sapInvoice.DocNum,
                reference);
        }
    }

    /// <summary>
    /// SAP's lines priced the way a receipt prices them: each unit at its tax-inclusive price rounded to the
    /// cent. Null when any line is missing that price.
    /// </summary>
    internal static decimal? RepricedAtReceiptPrices(InvoiceDto invoice)
    {
        var lines = invoice.Lines ?? [];

        if (lines.Count == 0 || lines.Any(line => line.PriceAfterVat <= 0m && line.LineTotal != 0m))
        {
            return null;
        }

        return lines.Sum(line => line.Quantity * Math.Round(line.PriceAfterVat, 2, MidpointRounding.AwayFromZero));
    }

    internal static bool Agrees(decimal receiptTotal, decimal sapTotal, decimal? repriced)
        => Math.Abs(receiptTotal - sapTotal) <= Tolerance
           || (repriced is decimal atReceiptPrices && Math.Abs(receiptTotal - atReceiptPrices) <= Tolerance);

    private async Task RaiseAsync(
        string reference,
        InvoiceDto sapInvoice,
        FiscalisedReceiptRecordDto receipt,
        decimal receiptTotal,
        decimal? repriced)
    {
        // One per sale. A post can be adopted again by a later run, and the second comparison says nothing new.
        var alreadyRaised = await db.ExceptionCenterIncidents
            .AsNoTracking()
            .AnyAsync(
                incident => incident.Source == ExceptionCenterSources.FiscalReceiptAmountMismatch
                            && incident.Reference == reference,
                CancellationToken.None);

        if (alreadyRaised)
        {
            return;
        }

        var currency = string.IsNullOrWhiteSpace(sapInvoice.DocCurrency) ? "USD" : sapInvoice.DocCurrency;
        var difference = sapInvoice.DocTotal - receiptTotal;
        var correction = difference > 0m
            ? $"ZIMRA holds {Money(difference)} less than SAP charged: file a debit note for {currency} {Money(difference)} against the receipt"
            : $"ZIMRA holds {Money(-difference)} more than SAP charged: file a credit note for {currency} {Money(-difference)} against the receipt";

        var message =
            $"Receipt {receipt.ReceiptGlobalNo} on device {receipt.DeviceId} for {reference} declares {currency} {Money(receiptTotal)}, but SAP invoice "
            + $"{sapInvoice.DocNum} is {currency} {Money(sapInvoice.DocTotal)}. {correction}, and find out why the two were "
            + "priced differently before more sales are signed the same way. A fiscal receipt cannot be withdrawn.";

        logger.LogError(
            "Fiscal receipt for {Reference} ({ReceiptTotal}) does not match SAP invoice {DocNum} ({SapTotal}).",
            reference,
            receiptTotal,
            sapInvoice.DocNum,
            sapInvoice.DocTotal);

        var now = DateTime.UtcNow;

        db.ExceptionCenterIncidents.Add(new ExceptionCenterIncidentEntity
        {
            Source = ExceptionCenterSources.FiscalReceiptAmountMismatch,
            Category = "Fiscalisation",
            Title = "Fiscal receipt does not match its SAP invoice",
            Reference = reference,
            Status = "RequiresReview",
            SourceSystem = "VanSales",
            Provider = "Fiscalisation",
            LastError = message.Length > 2000 ? message[..2000] : message,
            RetryCount = 0,
            MaxRetries = 0,
            // Nothing to resend: the receipt is with ZIMRA and the invoice is in SAP. The correction is a
            // document a person has to raise.
            CanRetry = false,
            CreatedAtUtc = now,
            OccurredAtUtc = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                Reference = reference,
                SapDocEntry = sapInvoice.DocEntry,
                SapDocNum = sapInvoice.DocNum,
                SapTotal = sapInvoice.DocTotal,
                SapTotalAtReceiptPrices = repriced,
                ReceiptDeviceId = receipt.DeviceId,
                ReceiptGlobalNo = receipt.ReceiptGlobalNo,
                ReceiptFiscalDay = receipt.FiscalDayNo,
                ReceiptId = receipt.ReceiptId,
                ReceiptTotal = receiptTotal,
                Difference = difference,
                Currency = currency
            })
        });

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
