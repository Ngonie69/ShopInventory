using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesAttendance.Queries.GetActiveVanVisit;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceStatus;

public sealed class GetVanSalesAttendanceStatusHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<GetVanSalesAttendanceStatusQuery, ErrorOr<VanSalesAttendanceStatusResponse>>
{
    public async Task<ErrorOr<VanSalesAttendanceStatusResponse>> Handle(
        GetVanSalesAttendanceStatusQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        // No open call is an answer — "checked out" — not a failure. Matched by code because the lookup
        // reports it as a validation error, not NotFound: testing the type sent every rep who was not on
        // site a failed read, and the handset then showed a stale check-in from its own table.
        var activeResult = await mediator.Send(new GetActiveVanVisitQuery(user.Id), cancellationToken);
        if (activeResult.IsError)
        {
            return activeResult.FirstError.Code == Errors.Timesheet.NoActiveCheckIn.Code
                ? VanSalesAttendanceMapper.MapStatusResponse(null, user)
                : activeResult.Errors;
        }

        return VanSalesAttendanceMapper.MapStatusResponse(activeResult.Value, user);
    }
}