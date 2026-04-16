using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.SearchBusinessPartners;

public sealed record SearchBusinessPartnersQuery(string SearchTerm) : IRequest<ErrorOr<BusinessPartnerListResponseDto>>;
