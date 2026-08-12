using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCurrentDay;

public sealed record GetVanSalesCurrentDayQuery(Guid UserId)
    : IRequest<ErrorOr<VanSalesRouteDayResponse>>;
