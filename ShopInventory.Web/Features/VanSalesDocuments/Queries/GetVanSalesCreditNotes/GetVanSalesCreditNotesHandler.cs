using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;

public sealed class GetVanSalesCreditNotesHandler(
    IVanSalesDocumentService documents,
    ILogger<GetVanSalesCreditNotesHandler> logger
) : IRequestHandler<GetVanSalesCreditNotesQuery, ErrorOr<VanSalesCreditNotesResponse>>
{
    public async Task<ErrorOr<VanSalesCreditNotesResponse>> Handle(
        GetVanSalesCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await documents.GetCreditNotesAsync(request.Filter, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading van sales credit notes");
            return Errors.VanSalesDocument.LoadFailed(ex.Message);
        }
    }
}
