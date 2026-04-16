using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.GetAllQuotations;

public sealed class GetAllQuotationsHandler(
    IQuotationService quotationService
) : IRequestHandler<GetAllQuotationsQuery, ErrorOr<QuotationListResponseDto>>
{
    public async Task<ErrorOr<QuotationListResponseDto>> Handle(
        GetAllQuotationsQuery request,
        CancellationToken cancellationToken)
    {
        var result = await quotationService.GetAllAsync(
            request.Page, request.PageSize, request.Status, request.CardCode,
            request.FromDate, request.ToDate, cancellationToken);
        return result;
    }
}
