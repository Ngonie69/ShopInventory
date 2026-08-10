using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

internal static class InvoiceFiscalTransactionSync
{
    private const string DocumentType = "Invoice";
    private const string SourceSystem = "InvoiceFiscalisationBackfill";
    private const string ConsolidationSourceSystem = "DesktopSaleConsolidation";
    private const string FiscalisedStatus = "Fiscalised";
    private const string NotFiscalisedStatus = "Not Fiscalised";
    private const string UnknownStatus = "Unknown";

    /// <summary>The Message column the explanation is written to.</summary>
    private const int MaxMessageLength = 2000;

    /// <summary>Receipts named in the message before it switches to a count.</summary>
    private const int MessageReceiptLimit = 12;

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

    /// <summary>
    /// Records an end-of-day consolidated invoice as already fiscalised, naming the receipts its
    /// constituent sales were fiscalised under before they reached SAP.
    /// </summary>
    /// <remarks>
    /// Without this row the consolidated invoice reads "Unknown" — nothing ever fiscalised that
    /// DocNum — and "Unknown" is what
    /// <see cref="QueueUnknownInvoicesForBackfill"/> queues and what leaves the Fiscalise button
    /// showing. Fiscalising it would send the same sales to FDMS a second time under a second
    /// invoice number, which the platform's (TaxPayerTIN, ReceiptType, InvoiceNo) idempotency key
    /// cannot catch and which no one can reverse.
    ///
    /// Best-effort by design. It runs after the SAP invoice has already posted, so a failure here
    /// must not fail the consolidation; the marker row in SaleConsolidations is what the guards
    /// actually depend on, and it is written with the DocNum itself.
    /// </remarks>
    public static async Task<bool> RecordConsolidatedInvoiceAsync(
        ISender sender,
        SaleConsolidationEntity consolidation,
        IReadOnlyCollection<ConsolidatedFiscalReceipt> receipts,
        string? currency,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var docNum = consolidation.SapDocNum.GetValueOrDefault();
        if (docNum <= 0)
        {
            return false;
        }

        var syncResult = await sender.Send(
            new SyncFiscalTransactionCommand(
                new SyncFiscalTransactionRequest
                {
                    ClientTransactionId = $"invoice-consolidation-{docNum}",
                    TimestampUtc = consolidation.PostedAt ?? DateTime.UtcNow,
                    DocNum = docNum,
                    DocumentType = DocumentType,
                    Status = FiscalisedStatus,
                    Message = BuildConsolidatedMessage(docNum, consolidation.SaleCount, receipts),
                    CardCode = consolidation.CardCode,
                    CardName = consolidation.CardName,
                    DocTotal = consolidation.TotalAmount,
                    VatSum = consolidation.TotalVat,
                    Currency = currency,
                    RawRequest = Serialize(new
                    {
                        consolidation.Id,
                        consolidation.CardCode,
                        consolidation.ConsolidationDate,
                        consolidation.SapDocEntry,
                        consolidation.SapDocNum,
                        consolidation.SaleCount
                    }),
                    // The full list, however long — the message only names the first few.
                    RawResponse = Serialize(receipts),
                    SourceSystem = ConsolidationSourceSystem
                },
                null,
                null),
            cancellationToken);

        if (syncResult.IsError)
        {
            logger.LogError(
                "Consolidated invoice {DocNum} could not be recorded as fiscalised: {Errors}. It will report as Unknown "
                + "until this is repaired, and must not be fiscalised — its {SaleCount} sale(s) already went to FDMS",
                docNum,
                string.Join("; ", syncResult.Errors.Select(error => error.Description)),
                consolidation.SaleCount);
            return false;
        }

        return true;
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

    private static string BuildConsolidatedMessage(
        int docNum,
        int saleCount,
        IReadOnlyCollection<ConsolidatedFiscalReceipt> receipts)
    {
        var named = receipts
            .Select(DescribeReceipt)
            .Where(description => !string.IsNullOrWhiteSpace(description))
            .ToList();

        var listed = string.Join(", ", named.Take(MessageReceiptLimit));
        if (named.Count > MessageReceiptLimit)
        {
            listed += $" and {named.Count - MessageReceiptLimit} more";
        }

        var message =
            $"Consolidation of {saleCount} desktop sale(s), each fiscalised before SAP under its own receipt"
            + (string.IsNullOrWhiteSpace(listed) ? ". " : $": {listed}. ")
            + $"Do not fiscalise invoice {docNum} — that would submit the same sales to FDMS again under a "
            + "second invoice number, which cannot be reversed.";

        return message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
    }

    private static string DescribeReceipt(ConsolidatedFiscalReceipt receipt)
    {
        var number = receipt.FiscalInvoiceNumber ?? receipt.Reference;
        if (string.IsNullOrWhiteSpace(number))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(receipt.ReceiptGlobalNo)
            ? number
            : $"{number} (receipt {receipt.ReceiptGlobalNo})";
    }

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}
