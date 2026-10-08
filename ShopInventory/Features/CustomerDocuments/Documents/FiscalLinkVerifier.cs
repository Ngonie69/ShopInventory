using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.Features.Invoices;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// Finds the fiscal receipt to print on an invoice sent to a customer, and refuses one that belongs to
/// a different document.
/// </summary>
/// <remarks>
/// <para>
/// The invoice download finds its receipt by DocNum, and a DocNum is no longer proof of anything:
/// after the September 2026 SAP update SAP reissued old DocEntries and DocNums, and local rows still
/// hold the old ones, which now name other customers' invoices. Staff reading a PDF can notice a
/// stranger's receipt; a customer sent one on WhatsApp is holding a wrong tax invoice. So this asks
/// the same three sources, in the same order, but makes each prove the receipt is this invoice's:
/// </para>
/// <list type="number">
/// <item>The invoice's own sale, found by the sale reference SAP keeps on it — immune to DocNum reuse —
/// and only if that sale does not record a different DocNum.</item>
/// <item>The fiscal transaction log by DocNum, only where the row is for the same customer, the same
/// total, is not a repost, and was signed no earlier than the day before the invoice.</item>
/// <item>The fiscal device by DocNum, last and only after a while, with the same date rule.</item>
/// </list>
/// <para>
/// A receipt that fails those checks is a <see cref="FiscalLinkVerdictKind.Mismatch"/>, which the
/// sender holds for a person rather than retrying: waiting will not change whose receipt it is.
/// </para>
/// </remarks>
internal static class FiscalLinkVerifier
{
    private const string InvoiceDocumentType = "Invoice";
    private const decimal TotalTolerance = 0.01m;

    public const string SaleReferenceSource = "SaleReference";
    public const string TransactionLogSource = "FiscalTransactions";
    public const string DeviceSource = "Device";

    public static async Task<FiscalLinkVerdict> VerifyAsync(
        ApplicationDbContext dbContext,
        IFiscalReceiptReader fiscalReceiptReader,
        FiscalLinkQuery query,
        bool allowDeviceLookup,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? mismatch = null;

        var bySale = await PerSaleInvoiceRegistry.FindReceiptByReferenceAsync(
            dbContext, query.SaleReference, cancellationToken);

        if (bySale is not null)
        {
            if (bySale.RecordedDocNum is { } recorded && recorded != query.DocNum)
            {
                mismatch = $"The sale {query.SaleReference} records invoice {recorded}, not {query.DocNum}.";
            }
            else
            {
                return FiscalLinkVerdict.Verified(
                    new InvoicePdfReceipt(
                        bySale.Receipt.QrCode,
                        bySale.Receipt.VerificationCode,
                        bySale.Receipt.FiscalDay,
                        bySale.Receipt.DeviceId?.ToString(CultureInfo.InvariantCulture),
                        bySale.Receipt.ReceiptGlobalNo),
                    SaleReferenceSource);
            }
        }

        var earliestSigning = EarliestSigningUtc(query.DocDate);

        var transactions = await dbContext.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.DocumentType == InvoiceDocumentType
                && transaction.DocNum == query.DocNum)
            .Where(FiscalDocumentStatusProjector.HasFiscalEvidenceExpression)
            .OrderByDescending(transaction => transaction.LastSyncedAtUtc)
            .ThenByDescending(transaction => transaction.TimestampUtc)
            .Select(transaction => new
            {
                transaction.CardCode,
                transaction.DocTotal,
                transaction.TimestampUtc,
                transaction.RepostedAfterSapUpdate,
                transaction.QRCode,
                transaction.VerificationCode,
                transaction.FiscalDay,
                transaction.DeviceId,
                transaction.ReceiptGlobalNo
            })
            .ToListAsync(cancellationToken);

        foreach (var transaction in transactions)
        {
            if (string.IsNullOrWhiteSpace(transaction.QRCode) && string.IsNullOrWhiteSpace(transaction.VerificationCode))
            {
                continue;
            }

            var refusal = transaction.RepostedAfterSapUpdate
                ? "it was filed for the invoice this number belonged to before the SAP update"
                : !SameCustomer(transaction.CardCode, query.CardCode)
                    ? $"it was filed for customer {transaction.CardCode}"
                    : !SameTotal(transaction.DocTotal, query)
                        ? $"it was filed for a total of {transaction.DocTotal.ToString("N2", CultureInfo.InvariantCulture)}"
                        : earliestSigning is { } earliest && transaction.TimestampUtc < earliest
                            ? $"it was signed on {transaction.TimestampUtc:yyyy-MM-dd}, before the invoice existed"
                            : null;

            if (refusal is null)
            {
                return FiscalLinkVerdict.Verified(
                    new InvoicePdfReceipt(
                        transaction.QRCode,
                        transaction.VerificationCode,
                        transaction.FiscalDay,
                        transaction.DeviceId,
                        transaction.ReceiptGlobalNo),
                    TransactionLogSource);
            }

            mismatch ??= $"A fiscal receipt is recorded under invoice number {query.DocNum}, but {refusal}.";
        }

        if (!allowDeviceLookup)
        {
            return mismatch is null
                ? FiscalLinkVerdict.NotYet("The invoice has no fiscal receipt yet.")
                : FiscalLinkVerdict.Mismatch(mismatch);
        }

        FiscalReceiptSnapshot? snapshot;
        try
        {
            snapshot = await fiscalReceiptReader.TryLookupAsync(
                query.DocNum,
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
            logger.LogWarning(ex, "Could not ask the fiscal device about invoice {DocNum}", query.DocNum);
            snapshot = null;
        }

        if (snapshot is { IsFiscalised: true }
            && (!string.IsNullOrWhiteSpace(snapshot.QrCode) || !string.IsNullOrWhiteSpace(snapshot.VerificationCode)))
        {
            if (earliestSigning is { } earliest && snapshot.TimestampUtc < earliest)
            {
                return FiscalLinkVerdict.Mismatch(
                    $"The fiscal device holds a receipt for invoice number {query.DocNum} signed on "
                    + $"{snapshot.TimestampUtc:yyyy-MM-dd}, before the invoice existed — it belongs to an earlier document with that number.");
            }

            return FiscalLinkVerdict.Verified(
                new InvoicePdfReceipt(
                    snapshot.QrCode,
                    snapshot.VerificationCode,
                    snapshot.FiscalDay,
                    snapshot.DeviceId,
                    snapshot.ReceiptGlobalNo),
                DeviceSource);
        }

        if (mismatch is not null)
        {
            return FiscalLinkVerdict.Mismatch(mismatch);
        }

        return snapshot is null
            ? FiscalLinkVerdict.NotYet("The fiscal device could not be asked about the invoice.")
            : FiscalLinkVerdict.NotYet("The invoice has not been fiscalised yet.");
    }

    /// <summary>
    /// The day before the invoice's date, at CAT midnight. A receipt is signed when its invoice is
    /// made or after, never before; a day's grace covers an invoice dated the day after it was signed.
    /// </summary>
    private static DateTime? EarliestSigningUtc(DateTime? docDate) =>
        docDate is { } date ? AuditService.FromCAT(date.Date.AddDays(-1)) : null;

    private static bool SameCustomer(string? recorded, string? invoice) =>
        string.IsNullOrWhiteSpace(recorded)
        || string.Equals(recorded.Trim(), invoice?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <remarks>
    /// A zero total on the log row says nothing (older rows did not record it), and the receipt may
    /// state either SAP total: the local one, or the document's own currency for a foreign-currency
    /// invoice.
    /// </remarks>
    private static bool SameTotal(decimal recorded, FiscalLinkQuery query)
    {
        if (recorded == 0m || (query.DocTotal is null && query.DocTotalFc is null))
        {
            return true;
        }

        return (query.DocTotal is { } local && Math.Abs(recorded - local) <= TotalTolerance)
            || (query.DocTotalFc is { } foreign && foreign != 0m && Math.Abs(recorded - foreign) <= TotalTolerance);
    }
}
