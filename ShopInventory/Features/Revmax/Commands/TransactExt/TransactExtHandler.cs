using System.Globalization;
using System.Net;
using System.Xml.Linq;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Revmax;
using ShopInventory.Models;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Commands.TransactExt;

public sealed class TransactExtHandler(
    IRevmaxClient revmaxClient,
    IOptions<RevmaxSettings> settings,
    IAuditService auditService,
    ApplicationDbContext dbContext,
    IHttpContextAccessor httpContextAccessor,
    ILogger<TransactExtHandler> logger
) : IRequestHandler<TransactExtCommand, ErrorOr<TransactMExtResponse>>
{
    private readonly RevmaxSettings _settings = settings.Value;

    public async Task<ErrorOr<TransactMExtResponse>> Handle(
        TransactExtCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (request is null)
        {
            const string error = "Request body is required";
            await RevmaxFiscalTransactionLog.TryRecordAsync(
                dbContext,
                httpContextAccessor,
                logger,
                "TransactMExt",
                null,
                "Failed",
                error,
                rawResponse: new { Error = error },
                cancellationToken: cancellationToken);

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.CreateRevmaxExtendedTransaction,
                RevmaxAudit.TransactionEntityType,
                null,
                error,
                false,
                error);

            return Errors.Revmax.InvalidRequest;
        }

        // Validate request
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            var validationMessage = string.Join("; ", validationErrors.Select(error => error.Description));
            await RevmaxFiscalTransactionLog.TryRecordAsync(
                dbContext,
                httpContextAccessor,
                logger,
                "TransactMExt",
                request,
                "Failed",
                validationMessage,
                rawResponse: new { Error = validationMessage },
                cancellationToken: cancellationToken);

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.CreateRevmaxExtendedTransaction,
                RevmaxAudit.TransactionEntityType,
                request.InvoiceNumber,
                validationMessage,
                false,
                validationMessage);
            return validationErrors;
        }

        try
        {
            RevmaxRequestNormalizer.ApplyDefaults(request, _settings.DefaultCurrency, _settings.DefaultBranchName);
            request.refDeviceId ??= _settings.DefaultRefDeviceId;

            // Process and validate items
            var itemsResult = ProcessAndValidateItems(request);
            if (itemsResult.IsError)
            {
                var itemError = string.Join("; ", itemsResult.Errors.Select(error => error.Description));
                await RevmaxFiscalTransactionLog.TryRecordAsync(
                    dbContext,
                    httpContextAccessor,
                    logger,
                    "TransactMExt",
                    request,
                    "Failed",
                    itemError,
                    rawResponse: new { Error = itemError },
                    cancellationToken: cancellationToken);

                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.CreateRevmaxExtendedTransaction,
                    RevmaxAudit.TransactionEntityType,
                    request.InvoiceNumber,
                    itemError,
                    false,
                    itemError);
                return itemsResult.Errors;
            }
            var normalizedItems = itemsResult.Value;
            var normalizedCurrencies = RevmaxStructuredPayloadParser.NormalizeCurrencies(
                request.CurrenciesXml,
                request.Currency,
                request.InvoiceAmount);
            request.ItemsXml = normalizedItems;
            request.CurrenciesXml = normalizedCurrencies;
            var upstreamRequest = RevmaxStructuredPayloadParser.BuildUpstreamRequest(request, normalizedItems, normalizedCurrencies);

            // Check if this is a credit note
            bool isCreditNote = !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber);

            if (isCreditNote)
            {
                InvoiceResponse? originalInvoice = null;
                var skipSameDeviceSourceValidation = false;

                try
                {
                    originalInvoice = await revmaxClient.GetInvoiceAsync(request.OriginalInvoiceNumber!, cancellationToken);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    skipSameDeviceSourceValidation = true;
                    logger.LogInformation(
                        ex,
                        "Original invoice {OriginalInvoiceNumber} was not found on the current REVMax device while fiscalizing credit note {CreditNoteNumber}; continuing with TransactMExt cross-device processing.",
                        request.OriginalInvoiceNumber,
                        request.InvoiceNumber);
                }

                if (!skipSameDeviceSourceValidation && (originalInvoice == null || !originalInvoice.Success))
                {
                    skipSameDeviceSourceValidation = true;
                    logger.LogInformation(
                        "Original invoice {OriginalInvoiceNumber} could not be resolved on the current REVMax device while fiscalizing credit note {CreditNoteNumber}; continuing with TransactMExt cross-device processing.",
                        request.OriginalInvoiceNumber,
                        request.InvoiceNumber);
                }

                if (!skipSameDeviceSourceValidation)
                {
                    bool hasFiscalEvidence = !string.IsNullOrWhiteSpace(originalInvoice!.QRcode) ||
                                             (originalInvoice.Data?.ReceiptGlobalNo > 0);

                    if (!hasFiscalEvidence)
                    {
                        var error = $"Original invoice not fiscalized: {request.OriginalInvoiceNumber}";
                        await RevmaxFiscalTransactionLog.TryRecordAsync(
                            dbContext,
                            httpContextAccessor,
                            logger,
                            "TransactMExt",
                            request,
                            "Failed",
                            error,
                            rawResponse: originalInvoice,
                            cancellationToken: cancellationToken);

                        await RevmaxAudit.TryLogAsync(
                            auditService,
                            AuditActions.CreateRevmaxExtendedTransaction,
                            RevmaxAudit.TransactionEntityType,
                            request.InvoiceNumber,
                            error,
                            false,
                            error);
                        return Errors.Revmax.TransactionFailed($"Original invoice not fiscalized: {request.OriginalInvoiceNumber}");
                    }

                    // Copy fiscal reference from original invoice if available
                    if (originalInvoice.Data != null)
                    {
                        if (int.TryParse(originalInvoice.FiscalDay, out var fiscalDay))
                        {
                            request.refFiscalDayNo ??= fiscalDay;
                        }
                        request.refReceiptGlobalNo ??= originalInvoice.Data.ReceiptGlobalNo;
                    }
                }

                // Check for duplicate credit note fiscalization
                var existingInvoice = await GetExistingInvoiceIfAvailableAsync(request.InvoiceNumber!, cancellationToken);
                if (existingInvoice is { Success: true })
                {
                    bool isDuplicate = !string.IsNullOrWhiteSpace(existingInvoice.QRcode) ||
                                       (existingInvoice.Data?.ReceiptGlobalNo > 0);
                    if (isDuplicate)
                    {
                        var error = $"Credit note already fiscalized: {request.InvoiceNumber}";
                        await RevmaxFiscalTransactionLog.TryRecordAsync(
                            dbContext,
                            httpContextAccessor,
                            logger,
                            "TransactMExt",
                            request,
                            "Fiscalised",
                            error,
                            rawResponse: existingInvoice,
                            cancellationToken: cancellationToken);

                        await RevmaxAudit.TryLogAsync(
                            auditService,
                            AuditActions.CreateRevmaxExtendedTransaction,
                            RevmaxAudit.TransactionEntityType,
                            request.InvoiceNumber,
                            error,
                            false,
                            error);
                        return Errors.Revmax.TransactionFailed($"Credit note already fiscalized: {request.InvoiceNumber}");
                    }
                }

                request.Istatus = "02";

                logger.LogInformation("Processing credit note (ext) {CreditNoteNumber} for original invoice {OriginalInvoiceNumber}",
                    request.InvoiceNumber, request.OriginalInvoiceNumber);
            }

            var result = await revmaxClient.TransactMExtAsync(upstreamRequest, cancellationToken);
            if (result is null)
            {
                const string error = "No response from device";
                await RevmaxFiscalTransactionLog.TryRecordAsync(
                    dbContext,
                    httpContextAccessor,
                    logger,
                    "TransactMExt",
                    request,
                    "Failed",
                    error,
                    rawResponse: new { Error = error },
                    cancellationToken: cancellationToken);

                await RevmaxAudit.TryLogAsync(
                    auditService,
                    AuditActions.CreateRevmaxExtendedTransaction,
                    RevmaxAudit.TransactionEntityType,
                    request.InvoiceNumber,
                    error,
                    false,
                    error);
                return Errors.Revmax.DeviceError(error);
            }

            var isSuccess = result.Success;
            var upstreamMessage = result.Message;
            var details = isSuccess
                ? $"Fiscalized REVMax extended {(isCreditNote ? "credit note" : "invoice")} {request.InvoiceNumber}{(string.IsNullOrWhiteSpace(result.ReceiptGlobalNo) ? string.Empty : $" with receipt #{result.ReceiptGlobalNo}")}."
                : RevmaxFailureDiagnostics.BuildHandledFailureMessage(request.InvoiceNumber, result, upstreamMessage);

            if (!isSuccess)
            {
                logger.LogWarning(
                    "REVMax upstream failure on {Endpoint} for invoice {InvoiceNumber} with code {Code}: {Message}",
                    "TransactMExt",
                    request.InvoiceNumber,
                    result.Code,
                    upstreamMessage);

                result.Message = details;
            }

            await RevmaxFiscalTransactionLog.TryRecordAsync(
                dbContext,
                httpContextAccessor,
                logger,
                "TransactMExt",
                request,
                isSuccess ? "Success" : "Failed",
                details,
                result,
                rawResponse: isSuccess
                    ? null
                    : RevmaxFailureDiagnostics.BuildHandledFailurePayload(
                        "TransactMExt",
                        request.InvoiceNumber,
                        result,
                        upstreamMessage,
                        details),
                cancellationToken: cancellationToken);

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.CreateRevmaxExtendedTransaction,
                RevmaxAudit.TransactionEntityType,
                request.InvoiceNumber,
                details,
                isSuccess,
                isSuccess ? null : details);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing extended transaction for invoice {InvoiceNumber}", request.InvoiceNumber);

            await RevmaxFiscalTransactionLog.TryRecordAsync(
                dbContext,
                httpContextAccessor,
                logger,
                "TransactMExt",
                request,
                "Failed",
                ex.Message,
                rawResponse: new { Error = ex.Message, Type = ex.GetType().Name },
                cancellationToken: cancellationToken);

            await RevmaxAudit.TryLogAsync(
                auditService,
                AuditActions.CreateRevmaxExtendedTransaction,
                RevmaxAudit.TransactionEntityType,
                request.InvoiceNumber,
                ex.Message,
                false,
                ex.Message);

            return Errors.Revmax.TransactionFailed(ex.Message);
        }
    }

    private async Task<InvoiceResponse?> GetExistingInvoiceIfAvailableAsync(
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            return await revmaxClient.GetInvoiceAsync(invoiceNumber, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "REVMax invoice {InvoiceNumber} was not found during duplicate fiscalization check", invoiceNumber);
            return null;
        }
    }

    private static List<Error> ValidateRequest(TransactMExtRequest request)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.InvoiceNumber))
            errors.Add(Error.Validation("Revmax.InvalidInvoiceNumber", "Invoice number is required"));

        if (!RevmaxStructuredPayloadParser.HasItems(request.ItemsXml))
            errors.Add(Error.Validation("Revmax.InvalidItems", "Items payload is required"));

        if (request.InvoiceAmount < 0)
            errors.Add(Error.Validation("Revmax.InvalidAmount", "Invoice amount must be >= 0"));

        if (request.InvoiceTaxAmount < 0)
            errors.Add(Error.Validation("Revmax.InvalidTaxAmount", "Invoice tax amount must be >= 0"));

        return errors;
    }

    private ErrorOr<List<RevmaxRequestItem>> ProcessAndValidateItems(TransactMRequest request)
        => RevmaxStructuredPayloadParser.NormalizeItems(request.ItemsXml, _settings.VatRate);
}
