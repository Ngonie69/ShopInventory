using ErrorOr;
using MediatR;
using ShopInventory.Common.Mobile;

namespace ShopInventory.Features.VanSalesAttendance.Commands.VanCheckIn;

/// <summary>
/// A van sales rep arriving at a call.
///
/// Its own command rather than a channel argument on the merchandiser's. The two operations are
/// measured on different things — a merchandiser on shelf time, a van on call compliance and takings
/// — read by different people, and they are free to diverge. A shared command with a flag is how the
/// two got mixed in the first place: every caller decided the flag for itself, and the ones that
/// forgot silently wrote or read the wrong operation's rows.
/// </summary>
public sealed record VanCheckInCommand(
    Guid UserId,
    string Username,
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    string? Notes,
    CaptureContext? Capture = null
) : IRequest<ErrorOr<VanCheckInResult>>;

public sealed record VanCheckInResult(
    int Id,
    DateTime CheckInTime,
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    bool WasReplay = false
);
