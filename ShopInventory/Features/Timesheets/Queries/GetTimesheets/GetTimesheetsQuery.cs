using ErrorOr;
using MediatR;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Timesheets.Queries.GetTimesheets;

/// <summary>
/// A page of visits.
/// </summary>
/// <remarks>
/// A null <c>Channel</c> returns both merchandiser and van sales visits, which is what the van sales
/// compatibility layer needs — a handset asking for its own attendance should get its own visits
/// whatever they were labelled, including the ones made before the column existed.
/// </remarks>
public sealed record GetTimesheetsQuery(
    int Page,
    int PageSize,
    Guid? UserId,
    string? Username,
    string? CustomerCode,
    DateTime? FromDate,
    DateTime? ToDate,
    TimesheetChannel? Channel = null
) : IRequest<ErrorOr<TimesheetListResult>>;

public sealed record TimesheetListResult(
    List<TimesheetEntryDto> Entries,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record TimesheetEntryDto(
    int Id,
    Guid UserId,
    string Username,
    string? FullName,
    string CustomerCode,
    string CustomerName,
    DateTime CheckInTime,
    DateTime? CheckOutTime,
    double? CheckInLatitude,
    double? CheckInLongitude,
    double? CheckOutLatitude,
    double? CheckOutLongitude,
    string? CheckInNotes,
    string? CheckOutNotes,
    double? DurationMinutes,
    TimesheetChannel Channel = TimesheetChannel.Merchandiser,
    string? CheckInLocationSource = null,
    string? CheckOutLocationSource = null,
    double? CheckInLocationAccuracyMetres = null,
    double? CheckOutLocationAccuracyMetres = null,
    string? LocationUnavailableReason = null,
    DateTime? CheckInRecordedAt = null,
    DateTime? CheckOutRecordedAt = null
)
{
    /// <summary>
    /// Whether this visit reached the server materially later than it happened — the handset was out
    /// of coverage and queued it.
    ///
    /// Computed here rather than projected, so it cannot be translated into SQL and cannot disagree
    /// with the two timestamps it reads. The threshold matches
    /// <see cref="Models.Entities.TimesheetEntryEntity.WasCapturedOffline"/>.
    /// </summary>
    public bool WasCapturedOffline =>
        IsLate(CheckInTime, CheckInRecordedAt) || IsLate(CheckOutTime, CheckOutRecordedAt);

    /// <summary>How long the record waited for signal, for a page that wants to say so.</summary>
    public TimeSpan? SyncDelay
    {
        get
        {
            var checkIn = Delay(CheckInTime, CheckInRecordedAt);
            var checkOut = Delay(CheckOutTime, CheckOutRecordedAt);

            if (checkIn is null) return checkOut;
            if (checkOut is null) return checkIn;

            return checkIn > checkOut ? checkIn : checkOut;
        }
    }

    private static TimeSpan? Delay(DateTime? occurred, DateTime? recorded) =>
        occurred.HasValue && recorded.HasValue ? recorded.Value - occurred.Value : null;

    private static bool IsLate(DateTime? occurred, DateTime? recorded) =>
        Delay(occurred, recorded) > TimeSpan.FromMinutes(2);
}
