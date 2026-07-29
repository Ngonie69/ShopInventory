using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Common.Fiscalization;

internal static class CreditNoteFiscalTransactionSync
{
    private const string DocumentType = "CreditNote";
    private const string SourceSystem = "CreditNote";
    private const string FiscalisedStatus = "Fiscalised";
    private const string NotFiscalisedStatus = "Not Fiscalised";

    public static async Task SyncAsync(
        CreditNoteDto creditNote,
        IRevmaxClient revmaxClient,
        ISender sender,
        ILogger logger,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (!creditNote.SAPDocNum.HasValue || creditNote.SAPDocNum.Value <= 0)
        {
            return;
        }

        InvoiceResponse? fiscalInvoice = null;

        try
        {
            fiscalInvoice = await revmaxClient.GetInvoiceAsync(
                creditNote.SAPDocNum.Value.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation(
                "Credit note {DocNum} is not present on REVMax after creation",
                creditNote.SAPDocNum.Value);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "REVMax lookup failed while syncing fiscal status for credit note {DocNum}",
                creditNote.SAPDocNum.Value);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unexpected REVMax lookup failure while syncing fiscal status for credit note {DocNum}",
                creditNote.SAPDocNum.Value);
            return;
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
                    ClientTransactionId = $"credit-note-fiscalisation-{creditNote.SAPDocNum.Value}",
                    TimestampUtc = timestampUtc,
                    DocNum = creditNote.SAPDocNum.Value,
                    DocumentType = DocumentType,
                    Status = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus,
                    Message = BuildMessage(creditNote.SAPDocNum.Value, isFiscalized, fiscalInvoice?.Message),
                    VerificationCode = isFiscalized ? fiscalInvoice?.VerificationCode : null,
                    QRCode = isFiscalized ? fiscalInvoice?.QRcode : null,
                    DeviceSerialNumber = isFiscalized ? fiscalInvoice?.DeviceSerialNumber : null,
                    DeviceId = isFiscalized ? fiscalInvoice?.DeviceID : null,
                    FiscalDay = isFiscalized ? fiscalInvoice?.FiscalDay : null,
                    ReceiptGlobalNo = receiptGlobalNo,
                    CardCode = creditNote.CardCode,
                    CardName = creditNote.CardName,
                    DocTotal = creditNote.DocTotal,
                    VatSum = creditNote.TaxAmount,
                    Currency = creditNote.Currency,
                    OriginalInvoiceNumber = creditNote.OriginalInvoiceDocEntry?.ToString(CultureInfo.InvariantCulture),
                    RawRequest = Serialize(new
                    {
                        creditNote.Id,
                        creditNote.CreditNoteNumber,
                        creditNote.SAPDocNum,
                        creditNote.OriginalInvoiceDocEntry
                    }),
                    RawResponse = Serialize(fiscalInvoice),
                    SourceSystem = SourceSystem
                },
                userId,
                null),
            cancellationToken);

        if (syncResult.IsError)
        {
            logger.LogWarning(
                "Failed to sync fiscal transaction row for credit note {DocNum}: {Errors}",
                creditNote.SAPDocNum.Value,
                string.Join("; ", syncResult.Errors.Select(error => error.Description)));
            return;
        }

        creditNote.IsFiscalized = isFiscalized;
        creditNote.FiscalizationStatus = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus;
        creditNote.FiscalReceiptGlobalNo = receiptGlobalNo;
        creditNote.FiscalizedAtUtc = isFiscalized ? timestampUtc : null;
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
            return $"Credit note {docNum} fiscalised successfully.";
        }

        if (!string.IsNullOrWhiteSpace(fallbackMessage))
        {
            return fallbackMessage.Trim();
        }

        return $"Credit note {docNum} is not fiscalised on REVMax.";
    }

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}