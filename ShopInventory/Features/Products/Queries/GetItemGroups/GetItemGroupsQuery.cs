using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Products.Queries.GetItemGroups;

public sealed record GetItemGroupsQuery : IRequest<ErrorOr<ItemGroupsListResponseDto>>;
