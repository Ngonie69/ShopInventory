using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartners;

public sealed record GetBusinessPartnersQuery() : IRequest<ErrorOr<BusinessPartnerListResponseDto>>;
