using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;

public sealed class GetInvoiceByDocEntryHandler(
    ApplicationDbContext dbContext,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetInvoiceByDocEntryHandler> logger
) : IRequestHandler<GetInvoiceByDocEntryQuery, ErrorOr<InvoiceDto>>
{
    public async Task<ErrorOr<InvoiceDto>> Handle(
        GetInvoiceByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        try
        {
            var invoice = await sapClient.GetInvoiceByDocEntryAsync(request.DocEntry, cancellationToken);
            if (invoice is null)
                return Errors.Invoice.NotFound(request.DocEntry);

            var invoiceDto = invoice.ToDto();
            await FiscalDocumentStatusProjector.EnrichInvoiceAsync(dbContext, invoiceDto, cancellationToken);
            return invoiceDto;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving invoice {DocEntry}", request.DocEntry);
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
