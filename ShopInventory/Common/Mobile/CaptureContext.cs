using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Mobile;

/// <summary>
/// What a handset reports about where and when an attendance event happened.
///
/// Lives here rather than beside either set of commands because both the merchandiser timesheet and
/// the van sales attendance paths take one, and neither should have to reference the other's
/// namespace to do it. It is a wire-shape, not business logic: it says what the client claimed, and
/// <see cref="CaptureClock"/> — the one place that decides how far to trust a client's clock — takes
/// it as its single argument.
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
