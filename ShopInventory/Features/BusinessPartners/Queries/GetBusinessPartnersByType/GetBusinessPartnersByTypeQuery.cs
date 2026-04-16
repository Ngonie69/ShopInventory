using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnersByType;

public sealed record GetBusinessPartnersByTypeQuery(string CardType) : IRequest<ErrorOr<BusinessPartnerListResponseDto>>;
