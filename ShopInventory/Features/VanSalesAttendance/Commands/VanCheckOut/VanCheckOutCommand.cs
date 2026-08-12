using ErrorOr;
using MediatR;
using ShopInventory.Common.Mobile;

namespace ShopInventory.Features.VanSalesAttendance.Commands.VanCheckOut;

/// <summary>
/// A van sales rep leaving a call. See <see cref="VanCheckIn.VanCheckInCommand"/> for why this is its
/// own command and not a flag on the merchandiser's.
/// </summary>
public sealed record VanCheckOutCommand(
    Guid UserId,
    string Username,
    double? Latitude,
    double? Longitude,
    string? Notes,
    CaptureContext? Capture = null
) : IRequest<ErrorOr<VanCheckOutResult>>;

public sealed record VanCheckOutResult(
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
