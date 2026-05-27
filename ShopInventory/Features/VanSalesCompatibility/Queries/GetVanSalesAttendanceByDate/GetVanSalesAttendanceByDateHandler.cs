using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Timesheets.Queries.GetTimesheets;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceByDate;

public sealed class GetVanSalesAttendanceByDateHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<GetVanSalesAttendanceByDateQuery, ErrorOr<VanSalesAttendanceByDateResponse>>
{
    public async Task<ErrorOr<VanSalesAttendanceByDateResponse>> Handle(
        GetVanSalesAttendanceByDateQuery query,
        CancellationToken cancellationToken)
    {
        if (!DateTime.TryParseExact(
                query.DateValue,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedCatDate))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidAttendanceDate",
                "Invalid date format. Use YYYY-MM-DD.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var startUtc = AuditService.FromCAT(parsedCatDate.Date);
        var endUtc = AuditService.FromCAT(parsedCatDate.Date.AddDays(1)).AddTicks(-1);

        var result = await mediator.Send(
            new GetTimesheetsQuery(1, 5000, user.Id, null, null, startUtc, endUtc),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesAttendanceMapper.MapByDateResponse(result.Value.Entries, user);
    }
}