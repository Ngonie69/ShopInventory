using ErrorOr;
using MediatR;
using ShopInventory.Common.Mobile;

namespace ShopInventory.Features.Timesheets.Commands.CheckIn;

/// <summary>
/// A merchandiser arriving at a shop.
///
/// Merchandiser only, and deliberately so — there is no channel argument to get wrong. A van sales
/// rep checks in through <c>Features/VanSalesAttendance</c>, which has its own command, its own
/// handler and its own endpoint. The two operations are measured on different things and are read by
/// different people, so nothing in this feature folder should ever answer for a van.
/// </summary>
public sealed record CheckInCommand(
    Guid UserId,
    string Username,
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    string? Notes,
    CaptureContext? Capture = null
) : IRequest<ErrorOr<CheckInResult>>;

public sealed record CheckInResult(
    int Id,
    DateTime CheckInTime,
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    bool WasReplay = false
);
