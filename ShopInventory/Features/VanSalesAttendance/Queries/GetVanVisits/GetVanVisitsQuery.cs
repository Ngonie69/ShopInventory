using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisits;

/// <summary>
/// A page of van sales calls.
///
/// No channel argument, by design — see <c>GetTimesheetsQuery</c>, which is pinned the same way in
/// the other direction. Between them there is no query in the system that can return both
/// operations' rows, whatever a caller asks for.
/// </summary>
public sealed record GetVanVisitsQuery(
    int Page,
    int PageSize,
    Guid? UserId,
    string? Username,
    string? CustomerCode,
    DateTime? FromDate,
    DateTime? ToDate
) : IRequest<ErrorOr<VanVisitListResult>>;

public sealed record VanVisitListResult(
    List<VanVisitDto> Entries,
    int TotalCount,
    int Page,
    int PageSize
);

/// <summary>
/// One van call. Its own shape rather than the merchandiser's <c>TimesheetEntryDto</c>: a van's row
/// is read on a page that groups by rep and round, and the two are free to grow different fields.
/// There is deliberately no channel property — nothing downstream should ever need to ask.
/// </summary>
public sealed record VanVisitDto(
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
    /// Whether this call reached the server materially later than it happened — the handset was out
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
