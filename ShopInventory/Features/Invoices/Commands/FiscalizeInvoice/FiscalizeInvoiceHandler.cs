using System.Text.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;

public sealed class FiscalizeInvoiceHandler(
    ISender sender,
    IFiscalizationService fiscalizationService,
    IAuditService auditService,
    ILogger<FiscalizeInvoiceHandler> logger
) : IRequestHandler<FiscalizeInvoiceCommand, ErrorOr<FiscalizationResult>>
{
    private const string DocumentType = "Invoice";
    private const string FiscalisedStatus = "Fiscalised";
    private const string SourceSystem = "InvoiceFiscalisationManual";

    public async Task<ErrorOr<FiscalizationResult>> Handle(
        FiscalizeInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        var invoiceResult = await sender.Send(new GetInvoiceByDocEntryQuery(command.DocEntry), cancellationToken);
        if (invoiceResult.IsError)
        {
            return invoiceResult.Errors;
        }

        var invoice = invoiceResult.Value;
        if (invoice.DocNum <= 0)
        {
            return Errors.Invoice.ValidationFailed("Only posted invoices can be fiscalised.");
        }

        if (string.Equals(invoice.FiscalizationStatus, FiscalisedStatus, StringComparison.OrdinalIgnoreCase))
        {
            var skippedResult = new FiscalizationResult
            {
                Success = true,
                Skipped = true,
                Message = $"Invoice {invoice.DocNum} is already fiscalised.",
                InvoiceNumber = invoice.DocNum.ToString()
            };

            await TryAuditAsync(invoice, skippedResult, command);
            return skippedResult;
        }

        var customerDetails = BuildCustomerDetails(invoice);
        var fiscalizationResult = await fiscalizationService.FiscalizeInvoiceAsync(
            invoice,
            customerDetails,
            cancellationToken);

        var syncResult = await sender.Send(
            new SyncFiscalTransactionCommand(
                BuildSyncRequest(invoice, customerDetails, fiscalizationResult),
                command.UserId?.ToString(),
                command.Username),
            cancellationToken);

        if (syncResult.IsError)
        {
            logger.LogWarning(
                "Manual fiscalization sync failed for invoice {DocNum}: {Errors}",
                invoice.DocNum,
                string.Join("; ", syncResult.Errors.Select(error => error.Description)));
        }

        await TryAuditAsync(invoice, fiscalizationResult, command);
        return fiscalizationResult;
    }

    private static CustomerFiscalDetails BuildCustomerDetails(InvoiceDto invoice)
        => new()
        {
            CustomerName = invoice.CardName,
            VatNumber = invoice.CustomerVatNo,
            Address = string.IsNullOrWhiteSpace(invoice.BillToAddress) ? invoice.ShipToAddress : invoice.BillToAddress,
            Telephone = invoice.CustomerPhone,
            Email = invoice.CustomerEmail,
            BPN = invoice.CustomerTinNumber
        };

    private static SyncFiscalTransactionRequest BuildSyncRequest(
        InvoiceDto invoice,
        CustomerFiscalDetails customerDetails,
        FiscalizationResult result)
    {
        var timestampUtc = DateTime.UtcNow;

        return new SyncFiscalTransactionRequest
        {
            ClientTransactionId = $"invoice-fiscalisation-manual-{invoice.DocNum}-{timestampUtc:yyyyMMddHHmmssfffffff}",
            TimestampUtc = timestampUtc,
            DocNum = invoice.DocNum,
            DocumentType = DocumentType,
            Status = ResolveStatus(result),
            Message = result.Message,
            VerificationCode = result.VerificationCode,
            QRCode = result.QRCode,
            DeviceSerialNumber = result.DeviceSerial,
            FiscalDay = result.FiscalDayNo,
            ReceiptGlobalNo = ParseReceiptGlobalNo(result.ReceiptGlobalNo),
            CardCode = invoice.CardCode,
            CardName = invoice.CardName,
            DocTotal = invoice.DocTotal,
            VatSum = invoice.VatSum,
            Currency = invoice.DocCurrency,
            RawRequest = result.RawRequestJson ?? JsonSerializer.Serialize(new
            {
                Invoice = invoice,
                CustomerDetails = customerDetails
            }),
            RawResponse = result.RawResponseJson ?? JsonSerializer.Serialize(result),
            SourceSystem = SourceSystem
        };
    }

    private static string ResolveStatus(FiscalizationResult result)
        => result.Skipped
            ? FiscalisedStatus
            : result.Success
                ? "Success"
                : "Failed";

    private static int? ParseReceiptGlobalNo(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private async Task TryAuditAsync(
        InvoiceDto invoice,
        FiscalizationResult result,
        FiscalizeInvoiceCommand command)
    {
        var isSuccess = result.Success || result.Skipped;
        var details = isSuccess
            ? $"Manual fiscalization processed for invoice #{invoice.DocNum}. {result.Message}"
            : $"Manual fiscalization failed for invoice #{invoice.DocNum}. {result.Message}";

        try
        {
            await auditService.LogAsync(
                AuditActions.FiscalizeInvoice,
                "Invoice",
                invoice.DocEntry.ToString(),
                details,
                isSuccess,
                isSuccess ? null : result.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to write audit log for manual fiscalization of invoice {DocNum} by {Username}",
                invoice.DocNum,
                command.Username);
        }
    }
}