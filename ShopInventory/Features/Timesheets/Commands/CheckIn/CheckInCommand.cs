using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Timesheets.Commands.CheckIn;

public sealed record CheckInCommand(
    Guid UserId,
    string Username,
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    string? Notes
) : IRequest<ErrorOr<CheckInResult>>;

public sealed record CheckInResult(
    int Id,
    DateTime CheckInTime,
    string CustomerCode,
    string CustomerName
);
