using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopIntegration.Commands.BackfillFiscalTransactions;

public sealed class BackfillFiscalTransactionsHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IFiscalisationApiClient fiscalisationClient,
    IFiscalDeviceConfigCache fiscalConfigCache,
    ISender sender,
    IOptions<SAPSettings> sapSettings,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    ILogger<BackfillFiscalTransactionsHandler> logger
) : IRequestHandler<BackfillFiscalTransactionsCommand, ErrorOr<BackfillFiscalTransactionsResult>>
{
    private const string DocumentType = "Invoice";
    private const string SourceSystem = "InvoiceFiscalisationBackfill";

    public async Task<ErrorOr<BackfillFiscalTransactionsResult>> Handle(
        BackfillFiscalTransactionsCommand command,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
        {
            return Errors.DesktopIntegration.SapDisabled;
        }

        if (!fiscalisationSettings.Value.Enabled)
        {
            return Errors.DesktopIntegration.BackfillFiscalTransactionsFailed("Fiscalisation is disabled.");
        }

        var request = command.Request;
        var fromUtc = NormalizeUtcDate(request.FromUtc) ?? DateTime.UtcNow.Date.AddDays(-30);
        var toUtc = NormalizeUtcDate(request.ToUtc) ?? DateTime.UtcNow.Date;

        try
        {
            var availableInvoiceCount = await sapClient.GetInvoicesCountAsync(
                docNum: null,
                cardCode: null,
                fromDate: fromUtc,
                toDate: toUtc,
                cancellationToken: cancellationToken);

            if (availableInvoiceCount <= 0)
            {
                return new BackfillFiscalTransactionsResult(
                    fromUtc,
                    toUtc,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0);
            }

            var scanLimit = Math.Min(availableInvoiceCount, request.MaxInvoices);
            var pageSize = Math.Min(request.PageSize, scanLimit);
            var scannedInvoiceCount = 0;
            var alreadyTrackedCount = 0;
            var fiscalisedFoundCount = 0;
            var transactionsSyncedCount = 0;
            var notFiscalisedCount = 0;
            var lookupFailedCount = 0;
            var syncFailedCount = 0;

            for (var skip = 0; skip < scanLimit;)
            {
                var remaining = scanLimit - scannedInvoiceCount;
                if (remaining <= 0)
                {
                    break;
                }

                var batchSize = Math.Min(pageSize, remaining);
                var invoices = await sapClient.GetPagedInvoicesByOffsetAsync(
                    skip,
                    batchSize,
                    docNum: null,
                    cardCode: null,
                    fromDate: fromUtc,
                    toDate: toUtc,
                    cancellationToken: cancellationToken);

                if (invoices.Count == 0)
                {
                    break;
                }

                skip += invoices.Count;

                var trackedDocNums = await ResolveTrackedInvoiceNumbersAsync(invoices, cancellationToken);

                foreach (var invoice in invoices)
                {
                    if (scannedInvoiceCount >= scanLimit)
                    {
                        break;
                    }

                    scannedInvoiceCount++;

                    if (invoice.DocNum <= 0)
                    {
                        lookupFailedCount++;
                        logger.LogWarning(
                            "Skipping fiscal transaction backfill for invoice DocEntry {DocEntry} because DocNum is invalid",
                            invoice.DocEntry);
                        continue;
                    }

                    if (trackedDocNums.Contains(invoice.DocNum))
                    {
                        alreadyTrackedCount++;
                        continue;
                    }

                    var snapshot = await FiscalReceiptLookup.TryLookupAsync(
                        fiscalisationClient,
                        fiscalConfigCache,
                        invoice.DocNum,
                        ReceiptType.FiscalInvoice,
                        logger,
                        cancellationToken);

                    if (snapshot is null)
                    {
                        // The lookup failed, which is not the same as "not fiscalised".
                        lookupFailedCount++;
                        continue;
                    }

                    if (!snapshot.IsFiscalised)
                    {
                        notFiscalisedCount++;
                        continue;
                    }

                    fiscalisedFoundCount++;

                    var syncResult = await sender.Send(
                        new SyncFiscalTransaction.SyncFiscalTransactionCommand(
                            BuildSyncRequest(invoice, snapshot, fromUtc, toUtc),
                            command.UserId,
                            command.Username),
                        cancellationToken);

                    if (syncResult.IsError)
                    {
                        syncFailedCount++;
                        logger.LogWarning(
                            "Failed to sync backfilled fiscal transaction row for invoice {DocNum}: {Errors}",
                            invoice.DocNum,
                            string.Join("; ", syncResult.Errors.Select(error => error.Description)));
                        continue;
                    }

                    transactionsSyncedCount++;
                }
            }

            logger.LogInformation(
                "Backfilled fiscal transactions for invoices from {FromUtc} to {ToUtc}. Available {Available}, scanned {Scanned}, synced {Synced}, already tracked {Tracked}, not fiscalised {NotFiscalised}, lookup failures {LookupFailed}, sync failures {SyncFailed}",
                fromUtc,
                toUtc,
                availableInvoiceCount,
                scannedInvoiceCount,
                transactionsSyncedCount,
                alreadyTrackedCount,
                notFiscalisedCount,
                lookupFailedCount,
                syncFailedCount);

            return new BackfillFiscalTransactionsResult(
                fromUtc,
                toUtc,
                availableInvoiceCount,
                scannedInvoiceCount,
                alreadyTrackedCount,
                fiscalisedFoundCount,
                transactionsSyncedCount,
                notFiscalisedCount,
                lookupFailedCount,
                syncFailedCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill fiscal transaction log");
            return Errors.DesktopIntegration.BackfillFiscalTransactionsFailed(ex.GetBaseException().Message);
        }
    }

    private async Task<HashSet<int>> ResolveTrackedInvoiceNumbersAsync(
        IReadOnlyCollection<Invoice> invoices,
        CancellationToken cancellationToken)
    {
        var docNums = invoices
            .Select(invoice => invoice.DocNum)
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (docNums.Count == 0)
        {
            return [];
        }

        var trackedDocNums = await dbContext.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.DocumentType == DocumentType
                && docNums.Contains(transaction.DocNum)
                && (transaction.Status == "Success"
                    || transaction.Status == "Fiscalised"
                    || transaction.ReceiptGlobalNo != null
                    || (transaction.QRCode != null && transaction.QRCode != string.Empty)))
            .Select(transaction => transaction.DocNum)
            .Distinct()
            .ToListAsync(cancellationToken);

        return trackedDocNums.ToHashSet();
    }

    private static SyncFiscalTransaction.SyncFiscalTransactionRequest BuildSyncRequest(
        Invoice invoice,
        FiscalReceiptSnapshot snapshot,
        DateTime fromUtc,
        DateTime toUtc)
    {
        return new SyncFiscalTransaction.SyncFiscalTransactionRequest
        {
            ClientTransactionId = $"invoice-fiscal-backfill-{invoice.DocNum}",
            TimestampUtc = snapshot.TimestampUtc,
            DocNum = invoice.DocNum,
            DocumentType = DocumentType,
            Status = "Fiscalised",
            Message = BuildMessage(invoice.DocNum, snapshot.ReceiptGlobalNo),
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
                InvoiceDocEntry = invoice.DocEntry,
                InvoiceDocNum = invoice.DocNum,
                WindowFromUtc = fromUtc,
                WindowToUtc = toUtc
            }),
            RawResponse = snapshot.RawResponseJson,
            SourceSystem = SourceSystem
        };
    }

    private static DateTime? NormalizeUtcDate(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc) : null;

    private static string BuildMessage(int docNum, int? receiptGlobalNo)
        => receiptGlobalNo.HasValue
            ? $"Backfilled fiscalised invoice {docNum} from existing fiscal receipt #{receiptGlobalNo}."
            : $"Backfilled fiscalised invoice {docNum} from an existing fiscal transaction.";

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}