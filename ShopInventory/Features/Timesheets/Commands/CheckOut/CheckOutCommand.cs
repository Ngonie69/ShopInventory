using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Timesheets.Commands.CheckOut;

public sealed record CheckOutCommand(
    Guid UserId,
    string Username,
    double? Latitude,
    double? Longitude,
    string? Notes
) : IRequest<ErrorOr<CheckOutResult>>;

public sealed record CheckOutResult(
    int Id,
    string CustomerCode,
    string CustomerName,
    DateTime CheckInTime,
    DateTime CheckOutTime,
    double DurationMinutes
);
