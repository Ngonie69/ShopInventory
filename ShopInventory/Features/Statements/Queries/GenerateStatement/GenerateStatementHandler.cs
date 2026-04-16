using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Statements.Queries.GenerateStatement;

public sealed class GenerateStatementHandler(
    IBusinessPartnerService businessPartnerService,
    IInvoiceService invoiceService,
    IIncomingPaymentService incomingPaymentService,
    IStatementService statementService,
    ILogger<GenerateStatementHandler> logger
) : IRequestHandler<GenerateStatementQuery, ErrorOr<GenerateStatementResult>>
{
    public async Task<ErrorOr<GenerateStatementResult>> Handle(
        GenerateStatementQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = request.FromDate ?? DateTime.Now.AddMonths(-3);
            var to = request.ToDate ?? DateTime.Now;

            var customer = await businessPartnerService.GetBusinessPartnerByCodeAsync(request.CardCode);
            if (customer is null)
                return Errors.Statement.CustomerNotFound(request.CardCode);

            var allInvoices = await invoiceService.GetInvoicesByCustomerAsync(request.CardCode);
            var incomingPayments = await incomingPaymentService.GetIncomingPaymentsByCustomerAsync(request.CardCode);

            var filteredInvoices = allInvoices
                .Where(i => IsValidInvoice(i) && FilterByDate(i.DocDate, from, to))
                .ToList();

            var allOpenInvoices = allInvoices
                .Where(i => IsValidInvoice(i) && i.DocStatus == "O" && (i.DocTotal - i.PaidToDate) > 0)
                .ToList();

            var filteredIncomingPayments = incomingPayments
                .Where(ip => FilterByDate(ip.DocDate, from, to))
                .OrderBy(p => GetDateValue(p.DocDate))
                .ToList();

            var pdfBytes = await statementService.GenerateCustomerStatementAsync(
                customer, filteredInvoices, filteredIncomingPayments, from, to, allOpenInvoices);

            var fileName = $"Statement_{request.CardCode}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";

            return new GenerateStatementResult(pdfBytes, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating statement for {CardCode}", request.CardCode);
            return Errors.Statement.GenerationFailed(ex.Message);
        }
    }

    private static bool IsValidInvoice(InvoiceDto invoice)
    {
        try
        {
            if (invoice.DocStatus == "X")
                return false;

            return invoice.DocTotal >= 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool FilterByDate(string? docDate, DateTime fromDate, DateTime toDate)
    {
        try
        {
            var date = GetDateValue(docDate);
            return date >= fromDate && date <= toDate.AddDays(1);
        }
        catch
        {
            return false;
        }
    }

    private static DateTime GetDateValue(string? docDate)
    {
        if (string.IsNullOrEmpty(docDate))
            return DateTime.MinValue;
        if (DateTime.TryParse(docDate, out var parsedDate))
            return parsedDate;
        return DateTime.MinValue;
    }
}
