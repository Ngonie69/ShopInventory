using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.BusinessPartners.Queries.GetBusinessPartnerGroups;

public sealed record GetBusinessPartnerGroupsQuery : IRequest<ErrorOr<BusinessPartnerGroupsListResponseDto>>;
