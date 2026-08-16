using System.Text.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;

public sealed class FiscalizeInvoiceHandler(
    ApplicationDbContext dbContext,
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

        // Before the already-fiscalised shortcut below, so the refusal says why rather than reporting
        // a bare skip, and before anything can reach the platform. FDMS submission is irreversible.
        var consolidation = await ConsolidatedInvoiceRegistry.FindByDocNumAsync(
            dbContext,
            invoice.DocNum,
            cancellationToken);

        if (consolidation is not null)
        {
            var refusal = Errors.Invoice.AlreadyFiscalisedAsConsolidatedSales(
                invoice.DocNum,
                consolidation.SaleCount);

            logger.LogWarning(
                "Refused manual fiscalisation of consolidated invoice {DocNum} requested by {Username}; it consolidates "
                + "{SaleCount} sale(s) already fiscalised pre-SAP",
                invoice.DocNum,
                command.Username,
                consolidation.SaleCount);

            await TryAuditAsync(invoice, isSuccess: false, refusal.Description, command);
            return refusal;
        }

        // The same refusal for the one-invoice-per-sale routes. Their sales are fiscalised under their
        // own external reference before reaching SAP, so a lookup by this DocNum finds nothing at the
        // platform and the shortcut below would not fire — the sale would be signed a second time.
        var fiscalisedSale = await PerSaleInvoiceRegistry.FindByDocNumAsync(
            dbContext,
            invoice.DocNum,
            cancellationToken);

        if (fiscalisedSale is not null)
        {
            var refusal = Errors.Invoice.AlreadyFiscalisedAsSale(
                invoice.DocNum,
                fiscalisedSale.ExternalReferenceId,
                fiscalisedSale.FiscalReceiptNumber);

            logger.LogWarning(
                "Refused manual fiscalisation of invoice {DocNum} requested by {Username}; it records sale "
                + "{ExternalReference}, already fiscalised pre-SAP as receipt {ReceiptNumber}",
                invoice.DocNum,
                command.Username,
                fiscalisedSale.ExternalReferenceId,
                fiscalisedSale.FiscalReceiptNumber);

            await TryAuditAsync(invoice, isSuccess: false, refusal.Description, command);
            return refusal;
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

    private Task TryAuditAsync(
        InvoiceDto invoice,
        FiscalizationResult result,
        FiscalizeInvoiceCommand command)
        => TryAuditAsync(invoice, result.Success || result.Skipped, result.Message, command);

    private async Task TryAuditAsync(
        InvoiceDto invoice,
        bool isSuccess,
        string? message,
        FiscalizeInvoiceCommand command)
    {
        var details = isSuccess
            ? $"Manual fiscalization processed for invoice #{invoice.DocNum}. {message}"
            : $"Manual fiscalization failed for invoice #{invoice.DocNum}. {message}";

        try
        {
            await auditService.LogAsync(
                AuditActions.FiscalizeInvoice,
                "Invoice",
                invoice.DocEntry.ToString(),
                details,
                isSuccess,
                isSuccess ? null : message);
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