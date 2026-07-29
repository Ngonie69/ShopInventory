using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Timesheets.Queries.GetTimesheets;

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

        var result = await mediator.Send(
            new GetTimesheetsQuery(1, 5000, user.Id, null, null, null, null),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return VanSalesAttendanceMapper.MapListResponse(result.Value, user);
    }
}