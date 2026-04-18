using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Timesheets.Queries.GetTimesheets;

public sealed record GetTimesheetsQuery(
    int Page,
    int PageSize,
    Guid? UserId,
    string? Username,
    string? CustomerCode,
    DateTime? FromDate,
    DateTime? ToDate
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
    double? DurationMinutes
);
