using System.Globalization;
using System.Net;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.BackfillFiscalTransactions;

public sealed class BackfillFiscalTransactionsHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IRevmaxClient revmaxClient,
    ISender sender,
    IOptions<SAPSettings> sapSettings,
    IOptions<RevmaxSettings> revmaxSettings,
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

        if (!revmaxSettings.Value.Enabled)
        {
            return Errors.DesktopIntegration.BackfillFiscalTransactionsFailed("REVMax integration is disabled.");
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

                    InvoiceResponse? fiscalInvoice;

                    try
                    {
                        fiscalInvoice = await revmaxClient.GetInvoiceAsync(
                            invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                            cancellationToken);
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        notFiscalisedCount++;
                        continue;
                    }
                    catch (HttpRequestException ex)
                    {
                        lookupFailedCount++;
                        logger.LogWarning(
                            ex,
                            "REVMax lookup failed while backfilling fiscal transaction log for invoice {DocNum}",
                            invoice.DocNum);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        lookupFailedCount++;
                        logger.LogWarning(
                            ex,
                            "Unexpected REVMax lookup error while backfilling fiscal transaction log for invoice {DocNum}",
                            invoice.DocNum);
                        continue;
                    }

                    if (!HasFiscalEvidence(fiscalInvoice))
                    {
                        notFiscalisedCount++;
                        continue;
                    }

                    fiscalisedFoundCount++;

                    var syncResult = await sender.Send(
                        new SyncFiscalTransaction.SyncFiscalTransactionCommand(
                            BuildSyncRequest(invoice, fiscalInvoice!, fromUtc, toUtc),
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
        InvoiceResponse fiscalInvoice,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var timestampUtc = ResolveTimestampUtc(fiscalInvoice.Data?.ReceiptDate);
        var receiptGlobalNo = ResolveReceiptGlobalNo(fiscalInvoice.Data?.ReceiptGlobalNo);

        return new SyncFiscalTransaction.SyncFiscalTransactionRequest
        {
            ClientTransactionId = $"invoice-fiscal-backfill-{invoice.DocNum}",
            TimestampUtc = timestampUtc,
            DocNum = invoice.DocNum,
            DocumentType = DocumentType,
            Status = "Fiscalised",
            Message = BuildMessage(invoice.DocNum, receiptGlobalNo),
            VerificationCode = fiscalInvoice.VerificationCode,
            QRCode = fiscalInvoice.QRcode,
            DeviceSerialNumber = fiscalInvoice.DeviceSerialNumber,
            DeviceId = fiscalInvoice.DeviceID,
            FiscalDay = fiscalInvoice.FiscalDay,
            ReceiptGlobalNo = receiptGlobalNo,
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
            RawResponse = Serialize(fiscalInvoice),
            SourceSystem = SourceSystem
        };
    }

    private static bool HasFiscalEvidence(InvoiceResponse? invoice)
        => invoice is { Success: true }
           && (!string.IsNullOrWhiteSpace(invoice.QRcode)
               || (invoice.Data?.ReceiptGlobalNo > 0));

    private static DateTime? NormalizeUtcDate(DateTime? value)
        => value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc) : null;

    private static DateTime ResolveTimestampUtc(string? receiptDate)
    {
        if (!string.IsNullOrWhiteSpace(receiptDate) &&
            DateTimeOffset.TryParse(receiptDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static int? ResolveReceiptGlobalNo(long? receiptGlobalNo)
        => receiptGlobalNo.HasValue && receiptGlobalNo.Value > 0 && receiptGlobalNo.Value <= int.MaxValue
            ? (int)receiptGlobalNo.Value
            : null;

    private static string BuildMessage(int docNum, int? receiptGlobalNo)
        => receiptGlobalNo.HasValue
            ? $"Backfilled fiscalised invoice {docNum} from existing REVMax receipt #{receiptGlobalNo}."
            : $"Backfilled fiscalised invoice {docNum} from an existing REVMax transaction.";

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}