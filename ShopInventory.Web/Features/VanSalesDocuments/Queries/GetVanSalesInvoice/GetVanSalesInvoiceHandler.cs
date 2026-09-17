using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;

public sealed class GetVanSalesInvoiceHandler(
    IVanSalesDocumentService documents,
    ILogger<GetVanSalesInvoiceHandler> logger
) : IRequestHandler<GetVanSalesInvoiceQuery, ErrorOr<VanSalesInvoiceDetailModel>>
{
    public async Task<ErrorOr<VanSalesInvoiceDetailModel>> Handle(
        GetVanSalesInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = await documents.GetInvoiceAsync(request.Reference, cancellationToken);
            return detail is null ? Errors.VanSalesDocument.NotFound(request.Reference) : detail;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading van sales invoice {Reference}", request.Reference);
            return Errors.VanSalesDocument.LoadFailed(ex.Message);
        }
    }
}
