using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationFromSAPByDocEntry;

public sealed record GetQuotationFromSAPByDocEntryQuery(
    int DocEntry
) : IRequest<ErrorOr<QuotationDto>>;
