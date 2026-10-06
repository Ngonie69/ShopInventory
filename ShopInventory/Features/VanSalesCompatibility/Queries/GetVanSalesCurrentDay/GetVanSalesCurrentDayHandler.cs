using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCurrentDay;

/// <summary>
/// The rep's open trading day, so the handset knows whether to offer Start Day or End Day.
///
/// Answers with a null <c>data</c> and a success status when there is no open day. That is the
/// ordinary state of every handset before the first tap of the morning, and returning an error for it
/// would make the app's normal startup look like a failure.
/// </summary>
public sealed class GetVanSalesCurrentDayHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanSalesCurrentDayQuery, ErrorOr<VanSalesRouteDayResponse>>
{
    public async Task<ErrorOr<VanSalesRouteDayResponse>> Handle(
        GetVanSalesCurrentDayQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        // Only a day End Day would close. A day older than that is abandoned, and reporting it as open
        // offered the rep End Day on it, which was refused, and hid Start Day behind it.
        var open = await VanRouteDayOpen
            .For(db.VanRouteDays.AsNoTracking(), query.UserId, AuditService.ToCAT(DateTime.UtcNow))
            .FirstOrDefaultAsync(cancellationToken);

        return VanSalesRouteDayMapper.Map(
            open,
            open is null ? "No open trading day." : "Open trading day retrieved.");
    }
}
