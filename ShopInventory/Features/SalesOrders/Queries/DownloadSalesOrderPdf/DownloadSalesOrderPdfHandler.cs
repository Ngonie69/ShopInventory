using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.DownloadSalesOrderPdf;

public sealed class DownloadSalesOrderPdfHandler(
    ISalesOrderService salesOrderService,
    ISalesOrderPdfService salesOrderPdfService,
    ISAPServiceLayerClient sapClient,
    ILogger<DownloadSalesOrderPdfHandler> logger
) : IRequestHandler<DownloadSalesOrderPdfQuery, ErrorOr<(byte[] PdfBytes, string FileName)>>
{
    public async Task<ErrorOr<(byte[] PdfBytes, string FileName)>> Handle(
        DownloadSalesOrderPdfQuery request,
        CancellationToken cancellationToken)
    {
        var order = request.UseLocal
            ? await salesOrderService.GetByIdFromLocalAsync(request.Id, cancellationToken)
            : await salesOrderService.GetByIdAsync(request.Id, cancellationToken);

        if (order is null)
        {
            return Errors.SalesOrder.NotFound(request.Id);
        }

        string? customerVatNo = null;
        string? customerTinNumber = null;
        string? customerPhone = null;
        string? customerEmail = null;

        if (!string.IsNullOrWhiteSpace(order.CardCode))
        {
            try
            {
                var businessPartner = await sapClient.GetBusinessPartnerByCodeAsync(order.CardCode, cancellationToken);
                if (businessPartner is not null)
                {
                    customerVatNo = businessPartner.VatRegNo;
                    customerTinNumber = businessPartner.TinNumber;
                    customerPhone = businessPartner.Phone1;
                    customerEmail = businessPartner.Email;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not enrich sales order {OrderNumber} PDF with business partner details", order.OrderNumber);
            }
        }

        try
        {
            var pdfBytes = await salesOrderPdfService.GenerateSalesOrderPdfAsync(
                order,
                customerVatNo,
                customerTinNumber,
                customerPhone,
                customerEmail);

            var fileName = $"SalesOrder_{order.OrderNumber}_{DateTime.UtcNow:yyyyMMdd}.pdf";
            return (pdfBytes, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating PDF for sales order {OrderNumber}", order.OrderNumber);
            return Errors.SalesOrder.CreationFailed("Failed to generate sales order PDF.");
        }
    }
}