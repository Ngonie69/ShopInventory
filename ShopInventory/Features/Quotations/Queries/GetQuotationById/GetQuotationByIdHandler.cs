using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationById;

public sealed class GetQuotationByIdHandler(
    IQuotationService quotationService
) : IRequestHandler<GetQuotationByIdQuery, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        GetQuotationByIdQuery request,
        CancellationToken cancellationToken)
    {
        var quotation = await quotationService.GetByIdAsync(request.Id, cancellationToken);
        if (quotation == null)
            return Errors.Quotation.NotFound(request.Id);

        return quotation;
    }
}
