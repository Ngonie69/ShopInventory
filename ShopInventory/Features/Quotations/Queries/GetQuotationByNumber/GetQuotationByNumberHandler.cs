using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationByNumber;

public sealed class GetQuotationByNumberHandler(
    IQuotationService quotationService
) : IRequestHandler<GetQuotationByNumberQuery, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        GetQuotationByNumberQuery request,
        CancellationToken cancellationToken)
    {
        var quotation = await quotationService.GetByQuotationNumberAsync(request.QuotationNumber, cancellationToken);
        if (quotation == null)
            return Errors.Quotation.NotFoundByNumber(request.QuotationNumber);

        return quotation;
    }
}
