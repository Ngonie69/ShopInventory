using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Logging;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Common.Fiscalization;

internal static class CreditNoteFiscalTransactionSync
{
    private const string DocumentType = "CreditNote";
    private const string SourceSystem = "CreditNote";
    private const string FiscalisedStatus = "Fiscalised";
    private const string NotFiscalisedStatus = "Not Fiscalised";

    public static async Task SyncAsync(
        CreditNoteDto creditNote,
        IFiscalisationApiClient client,
        IFiscalDeviceConfigCache configCache,
        ISender sender,
        ILogger logger,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (!creditNote.SAPDocNum.HasValue || creditNote.SAPDocNum.Value <= 0)
        {
            return;
        }

        var docNum = creditNote.SAPDocNum.Value;

        var snapshot = await FiscalReceiptLookup.TryLookupAsync(
            client, configCache, docNum, ReceiptType.CreditNote, logger, cancellationToken);

        if (snapshot is null)
        {
            // Lookup failed — leave the status alone rather than recording a guess.
            return;
        }

        var isFiscalized = snapshot.IsFiscalised;

        var syncResult = await sender.Send(
            new SyncFiscalTransactionCommand(
                new SyncFiscalTransactionRequest
                {
                    ClientTransactionId = $"credit-note-fiscalisation-{docNum}",
                    TimestampUtc = snapshot.TimestampUtc,
                    DocNum = docNum,
                    DocumentType = DocumentType,
                    Status = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus,
                    Message = BuildMessage(docNum, isFiscalized),
                    VerificationCode = snapshot.VerificationCode,
                    QRCode = snapshot.QrCode,
                    DeviceSerialNumber = snapshot.DeviceSerialNumber,
                    DeviceId = snapshot.DeviceId,
                    FiscalDay = snapshot.FiscalDay,
                    ReceiptGlobalNo = snapshot.ReceiptGlobalNo,
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
                    RawResponse = snapshot.RawResponseJson,
                    SourceSystem = SourceSystem
                },
                userId,
                null),
            cancellationToken);

        if (syncResult.IsError)
        {
            logger.LogWarning(
                "Failed to sync fiscal transaction row for credit note {DocNum}: {Errors}",
                docNum,
                string.Join("; ", syncResult.Errors.Select(error => error.Description)));
            return;
        }

        creditNote.IsFiscalized = isFiscalized;
        creditNote.FiscalizationStatus = isFiscalized ? FiscalisedStatus : NotFiscalisedStatus;
        creditNote.FiscalReceiptGlobalNo = snapshot.ReceiptGlobalNo;
        creditNote.FiscalizedAtUtc = isFiscalized ? snapshot.TimestampUtc : null;
    }

    private static string BuildMessage(int docNum, bool isFiscalized)
        => isFiscalized
            ? $"Credit note {docNum} fiscalised successfully."
            : $"Credit note {docNum} is not fiscalised.";

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}
