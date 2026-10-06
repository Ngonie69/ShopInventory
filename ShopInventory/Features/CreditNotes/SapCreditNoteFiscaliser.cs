using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes;

/// <summary>
/// Fiscalises a credit memo that is already in SAP, and records what happened.
/// </summary>
/// <remarks>
/// One routine for every caller that holds a SAP credit memo and owes ZIMRA its receipt: the approval
/// add, which fiscalises the memo it has just created, and <see cref="SapCreditNoteFiscalisationSweep"/>,
/// which fiscalises the ones keyed straight into SAP. Two copies of this would be two answers to which
/// receipt a credit reverses, and a credit note filed against the wrong original cannot be withdrawn.
///
/// Nothing here can fail its caller. The memo is already in SAP; every failure becomes a fiscal
/// transaction row (which the lists and the console read) and, where the caller asks, an Exception
/// Center incident.
/// </remarks>
public sealed class SapCreditNoteFiscaliser(
    ApplicationDbContext context,
    ISAPServiceLayerClient sap,
    IFiscalizationService fiscalizationService,
    ISender sender,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    ILogger<SapCreditNoteFiscaliser> logger)
{
    private const string FiscalDocumentType = "CreditNote";

    public async Task<SapCreditNoteFiscalisationOutcome> FiscaliseAsync(
        SAPCreditNote creditNote,
        SapCreditNoteFiscalisationCaller caller,
        CancellationToken cancellationToken)
    {
        // The line-level base entry is what a credit memo raised against an invoice carries; the header
        // one is not selected on credit notes. REVMax needs it to find the receipt being reversed; the
        // platform reads the link itself.
        var originalInvoiceDocEntry = creditNote.BaseEntry
            ?? creditNote.DocumentLines?.FirstOrDefault(line => line.BaseType == 13 && line.BaseEntry.HasValue)?.BaseEntry;

        var document = creditNote.ToFiscalDocument(creditNote.Comments);

        var customer = new CustomerFiscalDetails { CustomerName = creditNote.CardName };

        FiscalizationResult result;
        string? originalInvoiceNumber = null;

        try
        {
            // The receipt being reversed is filed under the invoice's DocNum — or, for a sale fiscalised
            // before SAP, under the sale's own reference — never under the BaseEntry this used to send.
            var original = await ResolveOriginalReceiptAsync(originalInvoiceDocEntry, cancellationToken);

            if (original.InvoiceNumber is not { } resolved)
            {
                var skipped = $"Fiscalisation skipped: {original.Refusal}";

                logger.LogWarning(
                    "Not fiscalising credit note {DocNum} ({Caller}): {Refusal}",
                    creditNote.DocNum, caller.Description, original.Refusal);

                // Recorded, so the console lists the memo as still owing a receipt and says why. Without
                // the row it is simply absent from everything that counts what ZIMRA is owed.
                await TryRecordFiscalTransactionAsync(
                    creditNote, document, null, new FiscalizationResult { Success = false, Message = skipped }, caller);

                await CaptureIncidentAsync(creditNote, caller.CaptureIncidents, skipped);

                return SapCreditNoteFiscalisationOutcome.Refused(skipped);
            }

            originalInvoiceNumber = resolved;
            result = await fiscalizationService.FiscalizeCreditNoteAsync(document, resolved, customer, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the reads before the submission throw — the fiscalisation services turn everything
            // from the submission itself into a result — so nothing was filed.
            logger.LogError(
                exception, "Error fiscalising credit note {DocNum} ({Caller})", creditNote.DocNum, caller.Description);

            if (!RevmaxReachability.IsNoAnswer(exception))
            {
                await TryRecordFiscalTransactionAsync(
                    creditNote,
                    document,
                    originalInvoiceNumber,
                    new FiscalizationResult { Success = false, Message = exception.Message },
                    caller);
            }

            await CaptureIncidentAsync(creditNote, caller.CaptureIncidents, exception.Message);

            return SapCreditNoteFiscalisationOutcome.Threw(exception);
        }

        // Nothing was sent: REVMax could not be asked whether it already holds a receipt, or the platform
        // is in a dry run. There is nothing to record — a "Failed" row would read as an attempt — and the
        // next pass asks again.
        if (result.ErrorCode == RevmaxHistoryFiscalizationService.HistoryUnavailableErrorCode
            || (result.Skipped && !result.Success))
        {
            await CaptureIncidentAsync(creditNote, caller.CaptureIncidents, result.Message);
            return SapCreditNoteFiscalisationOutcome.Filed(result);
        }

        await TryRecordFiscalTransactionAsync(creditNote, document, originalInvoiceNumber, result, caller);

        if (!result.Success && !result.Skipped)
        {
            // An unresolved outcome is always worth a person's look, whoever asked: it is the one case
            // where the next step must be a lookup and never another submission.
            await CaptureIncidentAsync(
                creditNote,
                caller.CaptureIncidents || result.RequiresReconciliation,
                result.Message ?? "Fiscalisation failed for the credit note.");
        }

        return SapCreditNoteFiscalisationOutcome.Filed(result);
    }

    /// <summary>
    /// The number the fiscal device holds the reversed invoice's receipt under.
    /// </summary>
    /// <remarks>
    /// See <see cref="CreditNoteOriginalReceipt"/>. The credit note carries only its base invoice's
    /// DocEntry, and the receipt is filed under the DocNum, so the invoice is read first.
    /// </remarks>
    private async Task<CreditNoteOriginalReceiptNumber> ResolveOriginalReceiptAsync(
        int? originalInvoiceDocEntry,
        CancellationToken cancellationToken)
    {
        if (originalInvoiceDocEntry is not > 0)
        {
            return CreditNoteOriginalReceiptNumber.Refused(
                "the credit note is not based on an invoice, so there is no receipt for it to reverse.");
        }

        var invoice = await sap.GetInvoiceByDocEntryAsync(originalInvoiceDocEntry.Value, cancellationToken);

        if (invoice is null)
        {
            return CreditNoteOriginalReceiptNumber.Refused(
                $"invoice DocEntry {originalInvoiceDocEntry} could not be read from SAP, so the receipt it reverses cannot be found.");
        }

        return await CreditNoteOriginalReceipt.ResolveAsync(
            context, invoice.DocNum, invoice.Comments, fiscalisationSettings.Value, cancellationToken);
    }

    /// <summary>
    /// The fiscal transaction row is what the Credit Notes list reads to say "Fiscalised"; without it a
    /// perfectly fiscalised document shows as owed a receipt.
    /// </summary>
    private async Task TryRecordFiscalTransactionAsync(
        SAPCreditNote creditNote,
        InvoiceDto document,
        string? originalInvoiceNumber,
        FiscalizationResult result,
        SapCreditNoteFiscalisationCaller caller)
    {
        try
        {
            var timestampUtc = DateTime.UtcNow;
            var recorded = await sender.Send(
                new SyncFiscalTransactionCommand(
                    new SyncFiscalTransactionRequest
                    {
                        ClientTransactionId = $"{caller.ClientTransactionIdPrefix}-{creditNote.DocNum}-{timestampUtc:yyyyMMddHHmmssfffffff}",
                        TimestampUtc = timestampUtc,
                        DocNum = creditNote.DocNum,
                        DocumentType = FiscalDocumentType,
                        Status = FiscalTransactionStatus.Of(result),
                        Message = result.RequiresReconciliation
                            ? FiscalOutcomeMessages.MarkUnresolved(result.Message)
                            : result.Message,
                        VerificationCode = result.VerificationCode,
                        QRCode = result.QRCode,
                        DeviceSerialNumber = result.DeviceSerial,
                        FiscalDay = result.FiscalDayNo,
                        ReceiptGlobalNo = int.TryParse(result.ReceiptGlobalNo, out var receiptNo) && receiptNo > 0 ? receiptNo : null,
                        CardCode = creditNote.CardCode,
                        CardName = creditNote.CardName,
                        DocTotal = document.DocTotal,
                        VatSum = document.VatSum,
                        Currency = creditNote.DocCurrency,
                        OriginalInvoiceNumber = string.IsNullOrWhiteSpace(originalInvoiceNumber) ? null : originalInvoiceNumber,
                        RawRequest = JsonSerializer.Serialize(new { Document = document }),
                        RawResponse = JsonSerializer.Serialize(result),
                        SourceSystem = caller.SourceSystem
                    },
                    caller.UserId,
                    caller.Username),
                CancellationToken.None);

            if (recorded.IsError)
            {
                logger.LogWarning(
                    "Fiscal transaction for credit note {DocNum} was not recorded: {Errors}",
                    creditNote.DocNum,
                    string.Join("; ", recorded.Errors.Select(error => error.Description)));
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Fiscal transaction for credit note {DocNum} was not recorded", creditNote.DocNum);
        }
    }

    private Task CaptureIncidentAsync(SAPCreditNote creditNote, bool capture, string? message) =>
        capture
            ? CreditNoteFiscalisationIncidents.CaptureAsync(
                context, logger, $"SAP-CN-{creditNote.DocNum}", creditNote.DocNum, creditNote.CardCode ?? string.Empty,
                message ?? "Fiscalisation failed for the credit note.", CancellationToken.None)
            : Task.CompletedTask;
}

/// <summary>Who is fiscalising a SAP credit memo, as its fiscal transaction row and log lines record it.</summary>
/// <param name="SourceSystem">The fiscal transaction row's source.</param>
/// <param name="ClientTransactionIdPrefix">Prefixes the row's client transaction id.</param>
/// <param name="Description">Names the caller in log lines.</param>
/// <param name="CaptureIncidents">
/// Whether a failure raises an Exception Center incident. An unresolved outcome always does.
/// </param>
/// <param name="UserId">Who the row is recorded against, when a person asked.</param>
/// <param name="Username">That person's username.</param>
public sealed record SapCreditNoteFiscalisationCaller(
    string SourceSystem,
    string ClientTransactionIdPrefix,
    string Description,
    bool CaptureIncidents,
    string? UserId = null,
    string? Username = null);

/// <summary>What became of one attempt to fiscalise a SAP credit memo.</summary>
/// <param name="Result">The fiscal service's answer, when it was asked.</param>
/// <param name="Refusal">Why it was not asked, when the original receipt could not be named.</param>
/// <param name="Exception">What stopped it before the submission, when something did.</param>
public sealed record SapCreditNoteFiscalisationOutcome(
    FiscalizationResult? Result,
    string? Refusal,
    Exception? Exception)
{
    public static SapCreditNoteFiscalisationOutcome Filed(FiscalizationResult result) => new(result, null, null);

    public static SapCreditNoteFiscalisationOutcome Refused(string refusal) => new(null, refusal, null);

    public static SapCreditNoteFiscalisationOutcome Threw(Exception exception) => new(null, null, exception);
}
