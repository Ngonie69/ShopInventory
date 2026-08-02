using System.Globalization;
using System.Net;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

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
    /// This used to do the REVMax lookups here, in sequence, inside the caller's request. Each one
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
        IRevmaxClient revmaxClient,
        ISender sender,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (invoice is null || invoice.DocNum <= 0)
        {
            return false;
        }

        InvoiceResponse? fiscalInvoice = null;

        try
        {
            fiscalInvoice = await revmaxClient.GetInvoiceAsync(
                invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation(
                "Invoice {DocNum} is not present on REVMax while backfilling fiscal status",
                invoice.DocNum);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "REVMax lookup failed while backfilling fiscal status for invoice {DocNum}",
                invoice.DocNum);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unexpected REVMax lookup failure while backfilling fiscal status for invoice {DocNum}",
                invoice.DocNum);
            return false;
        }

        var receiptGlobalNo = ResolveReceiptGlobalNo(fiscalInvoice?.Data?.ReceiptGlobalNo);
        var isFiscalized = HasFiscalEvidence(fiscalInvoice, receiptGlobalNo);
        var timestampUtc = isFiscalized
            ? ResolveTimestampUtc(fiscalInvoice?.Data?.ReceiptDate)
            : DateTime.UtcNow;

        var syncResult = await sender.Send(
            new SyncFiscalTransactionCommand(
                new SyncFiscalTransactionRequest
                {
                    ClientTransactionId = $"invoice-status-backfill-{invoice.DocNum}",
                    TimestampUtc = timestampUtc,
                    DocNum = invoice.DocNum,
                    DocumentType = DocumentType,
                    Status = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus,
                    Message = BuildMessage(invoice.DocNum, isFiscalized, fiscalInvoice?.Message),
                    VerificationCode = isFiscalized ? fiscalInvoice?.VerificationCode : null,
                    QRCode = isFiscalized ? fiscalInvoice?.QRcode : null,
                    DeviceSerialNumber = isFiscalized ? fiscalInvoice?.DeviceSerialNumber : null,
                    DeviceId = isFiscalized ? fiscalInvoice?.DeviceID : null,
                    FiscalDay = isFiscalized ? fiscalInvoice?.FiscalDay : null,
                    ReceiptGlobalNo = receiptGlobalNo,
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
                    RawResponse = Serialize(fiscalInvoice),
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
        invoice.FiscalQrCode = isFiscalized ? fiscalInvoice?.QRcode : null;
        invoice.FiscalReceiptGlobalNo = receiptGlobalNo;
        invoice.FiscalizedAtUtc = isFiscalized ? timestampUtc : null;
        return true;
    }

    private static bool HasFiscalEvidence(InvoiceResponse? fiscalInvoice, int? receiptGlobalNo)
        => fiscalInvoice is { Success: true }
           && (!string.IsNullOrWhiteSpace(fiscalInvoice.QRcode) || receiptGlobalNo.HasValue);

    private static DateTime ResolveTimestampUtc(string? receiptDate)
    {
        if (!string.IsNullOrWhiteSpace(receiptDate)
            && DateTimeOffset.TryParse(receiptDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static int? ResolveReceiptGlobalNo(long? receiptGlobalNo)
        => receiptGlobalNo.HasValue && receiptGlobalNo.Value > 0 && receiptGlobalNo.Value <= int.MaxValue
            ? (int)receiptGlobalNo.Value
            : null;

    private static string BuildMessage(int docNum, bool isFiscalized, string? fallbackMessage)
    {
        if (isFiscalized)
        {
            return $"Backfilled fiscalised invoice {docNum} from REVMax.";
        }

        if (!string.IsNullOrWhiteSpace(fallbackMessage))
        {
            return fallbackMessage.Trim();
        }

        return $"Invoice {docNum} is not fiscalised on REVMax.";
    }

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}