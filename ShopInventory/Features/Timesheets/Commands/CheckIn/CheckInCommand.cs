using ErrorOr;
using MediatR;
using ShopInventory.Common.Mobile;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Timesheets.Commands.CheckIn;

/// <summary>
/// What a handset reports about where and when an attendance event happened.
///
/// Separate from the command so a check-in and a check-out say it the same way, and so the one place
/// that decides how far to trust a client's clock — <see cref="CaptureClock"/> — has a single argument
/// to work on.
/// </summary>
/// <param name="OccurredAt">
/// When the rep tapped, as the handset reports it. Null for a live request, where the server's own
/// clock is both available and better.
/// </param>
/// <param name="ClientReference">The handset's id for this event, so a replay is not a second visit.</param>
/// <param name="LocationSource">One of <see cref="TimesheetLocationSources"/>.</param>
/// <param name="AccuracyMetres">The fix's own uncertainty, where the platform reported one.</param>
/// <param name="LocationUnavailableReason">Why there are no coordinates, on a record that has none.</param>
public sealed record CaptureContext(
    DateTime? OccurredAt = null,
    string? ClientReference = null,
    string? LocationSource = null,
    double? AccuracyMetres = null,
    string? LocationUnavailableReason = null)
{
    public static readonly CaptureContext Live = new();
}

public sealed record CheckInCommand(
    Guid UserId,
    string Username,
    string CustomerCode,
    string CustomerName,
    double? Latitude,
    double? Longitude,
    string? Notes,
    TimesheetChannel Channel = TimesheetChannel.Merchandiser,
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
