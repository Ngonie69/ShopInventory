using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;

public sealed record GetPricesByBusinessPartnerQuery(
    string CardCode,
    bool ForceRefresh,
    IReadOnlyCollection<string>? ItemCodes = null,
    bool UseLivePricing = true
) : IRequest<ErrorOr<ItemPricesByListResponseDto>>;
