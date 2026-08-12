using ErrorOr;
using MediatR;
using ShopInventory.Features.Timesheets.Commands.CheckIn;

namespace ShopInventory.Features.Timesheets.Commands.CheckOut;

public sealed record CheckOutCommand(
    Guid UserId,
    string Username,
    double? Latitude,
    double? Longitude,
    string? Notes,
    CaptureContext? Capture = null
) : IRequest<ErrorOr<CheckOutResult>>;

public sealed record CheckOutResult(
    int Id,
    string CustomerCode,
    string CustomerName,
    DateTime CheckInTime,
    DateTime CheckOutTime,
    double DurationMinutes,
    double? Latitude,
    double? Longitude,
    bool WasReplay = false
);
