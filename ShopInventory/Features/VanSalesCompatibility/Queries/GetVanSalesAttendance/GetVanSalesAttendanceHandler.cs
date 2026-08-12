using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesAttendance.Queries.GetVanVisits;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendance;

public sealed class GetVanSalesAttendanceHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<GetVanSalesAttendanceQuery, ErrorOr<VanSalesAttendanceListResponse>>
{
    public async Task<ErrorOr<VanSalesAttendanceListResponse>> Handle(
        GetVanSalesAttendanceQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        // The van's own query, not the merchandiser one. This used to go through GetTimesheetsQuery
        // with no channel, which returned whatever the user had of either kind.
        var result = await mediator.Send(
            new GetVanVisitsQuery(1, 5000, user.Id, null, null, null, null),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesAttendanceMapper.MapListResponse(result.Value, user);
    }
}