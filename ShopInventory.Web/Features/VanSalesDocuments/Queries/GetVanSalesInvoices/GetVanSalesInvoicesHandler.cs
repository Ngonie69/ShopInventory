using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;

public sealed class GetVanSalesInvoicesHandler(
    IVanSalesDocumentService documents,
    ILogger<GetVanSalesInvoicesHandler> logger
) : IRequestHandler<GetVanSalesInvoicesQuery, ErrorOr<VanSalesInvoicesResponse>>
{
    public async Task<ErrorOr<VanSalesInvoicesResponse>> Handle(
        GetVanSalesInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await documents.GetInvoicesAsync(request.Filter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading van sales invoices");
            return Errors.VanSalesDocument.LoadFailed(ex.Message);
        }
    }
}
