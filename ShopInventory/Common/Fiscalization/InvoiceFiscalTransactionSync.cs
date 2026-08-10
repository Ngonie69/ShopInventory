using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

internal static class InvoiceFiscalTransactionSync
{
    private const string DocumentType = "Invoice";
    private const string SourceSystem = "InvoiceFiscalisationBackfill";
    private const string FiscalisedStatus = "Fiscalised";
    private const string NotFiscalisedStatus = "Not Fiscalised";
    private const string UnknownStatus = "Unknown";

    /// <summary>
    /// Hands every invoice whose fiscal status is still unknown to the backfill queue, and reports
    /// how many were accepted.
    /// </summary>
    /// <remarks>
    /// This used to do the fiscal lookups here, in sequence, inside the caller's request. Each one
    /// takes around three seconds, and the cap was 100 per page — so a page of invoices nobody had
    /// looked at before could hold a user for over five minutes, and on 2026-08-02 one held a page
    /// of 100 for 152 seconds across 46 lookups.
    ///
    /// Nothing the page renders depends on the lookup having finished. The status shown comes from
    /// the local projection, and "Unknown" is the honest answer until the read-back lands; queueing
    /// makes that a few seconds rather than never, without a user waiting on it.
    /// </remarks>
    public static int QueueUnknownInvoicesForBackfill(
        IEnumerable<InvoiceDto>? invoices,
        IInvoiceFiscalStatusBackfillQueue queue)
    {
        if (invoices is null)
        {
            return 0;
        }

        return invoices
            .Where(invoice => invoice.DocNum > 0
                && string.Equals(invoice.FiscalizationStatus, UnknownStatus, StringComparison.OrdinalIgnoreCase))
            .GroupBy(invoice => invoice.DocNum)
            .Select(group => group.First())
            .Count(queue.TryQueue);
    }

    public static async Task<bool> SyncAsync(
        InvoiceDto? invoice,
        IFiscalisationApiClient client,
        IFiscalDeviceConfigCache configCache,
        ISender sender,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (invoice is null || invoice.DocNum <= 0)
        {
            return false;
        }

        var snapshot = await FiscalReceiptLookup.TryLookupAsync(
            client, configCache, invoice.DocNum, ReceiptType.FiscalInvoice, logger, cancellationToken);

        if (snapshot is null)
        {
            // The lookup itself failed. Leave the status as it was — recording "not fiscalised"
            // here would be a guess, and the queue will try again.
            return false;
        }

        var isFiscalized = snapshot.IsFiscalised;

        var syncResult = await sender.Send(
            new SyncFiscalTransactionCommand(
                new SyncFiscalTransactionRequest
                {
                    ClientTransactionId = $"invoice-status-backfill-{invoice.DocNum}",
                    TimestampUtc = snapshot.TimestampUtc,
                    DocNum = invoice.DocNum,
                    DocumentType = DocumentType,
                    Status = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus,
                    Message = BuildMessage(invoice.DocNum, isFiscalized),
                    VerificationCode = snapshot.VerificationCode,
                    QRCode = snapshot.QrCode,
                    DeviceSerialNumber = snapshot.DeviceSerialNumber,
                    DeviceId = snapshot.DeviceId,
                    FiscalDay = snapshot.FiscalDay,
                    ReceiptGlobalNo = snapshot.ReceiptGlobalNo,
                    CardCode = invoice.CardCode,
                    CardName = invoice.CardName,
                    DocTotal = invoice.DocTotal,
                    VatSum = invoice.VatSum,
                    Currency = invoice.DocCurrency,
                    RawRequest = Serialize(new
                    {
                        invoice.DocEntry,
                        invoice.DocNum,
                        invoice.CardCode,
                        invoice.CardName
                    }),
                    RawResponse = snapshot.RawResponseJson,
                    SourceSystem = SourceSystem
                },
                null,
                null),
            cancellationToken);

        if (syncResult.IsError)
        {
            logger.LogWarning(
                "Failed to sync fiscal transaction row for invoice {DocNum}: {Errors}",
                invoice.DocNum,
                string.Join("; ", syncResult.Errors.Select(error => error.Description)));
            return false;
        }

        invoice.IsFiscalized = isFiscalized;
        invoice.FiscalizationStatus = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus;
        invoice.FiscalQrCode = snapshot.QrCode;
        invoice.FiscalReceiptGlobalNo = snapshot.ReceiptGlobalNo;
        invoice.FiscalizedAtUtc = isFiscalized ? snapshot.TimestampUtc : null;
        return true;
    }

    private static string BuildMessage(int docNum, bool isFiscalized)
        => isFiscalized
            ? $"Backfilled fiscalised invoice {docNum} from the fiscalisation platform."
            : $"Invoice {docNum} is not fiscalised.";

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}
