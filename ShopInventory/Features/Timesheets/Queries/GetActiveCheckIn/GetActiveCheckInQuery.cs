using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Timesheets.Queries.GetActiveCheckIn;

public sealed record GetActiveCheckInQuery(
    Guid UserId
) : IRequest<ErrorOr<ActiveCheckInResult>>;

public sealed record ActiveCheckInResult(
    int Id,
    string CustomerCode,
    string CustomerName,
    DateTime CheckInTime,
    double? Latitude,
    double? Longitude,
    string? Notes
);
