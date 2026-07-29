using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Timesheets.Queries.GetActiveCheckIn;

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

        var activeResult = await mediator.Send(new GetActiveCheckInQuery(user.Id), cancellationToken);
        if (activeResult.IsError)
        {
            return activeResult.FirstError.Type == ErrorType.NotFound
                ? VanSalesAttendanceMapper.MapStatusResponse(null, user)
                : activeResult.Errors;
        }

        return VanSalesAttendanceMapper.MapStatusResponse(activeResult.Value, user);
    }
}