using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Queries.GetDayStatus;

public sealed record GetDayStatusQuery() : IRequest<ErrorOr<DayStatusResponse>>;
