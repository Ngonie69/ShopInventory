using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using static ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule.DesktopSalesReviewScheduleKeys;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule;

public sealed class GetDesktopSalesReviewScheduleHandler(ApplicationDbContext db)
    : IRequestHandler<GetDesktopSalesReviewScheduleQuery, ErrorOr<DesktopSalesReviewSchedule>>
{
    public async Task<ErrorOr<DesktopSalesReviewSchedule>> Handle(
        GetDesktopSalesReviewScheduleQuery request, CancellationToken cancellationToken)
    {
        var rows = await ReadAsync(db, cancellationToken);

        Guid? sendAs = rows.TryGetValue(SendAsUserId, out var stored) && Guid.TryParse(stored.Value, out var id) ? id : null;
        var owner = sendAs is null
            ? null
            : await db.Users.AsNoTracking()
                .Where(user => user.Id == sendAs)
                .Select(user => new { user.Username, user.FirstName, user.LastName })
                .FirstOrDefaultAsync(cancellationToken);
        var fullName = $"{owner?.FirstName} {owner?.LastName}".Trim();
        var sendAsName = owner is null ? null : fullName.Length > 0 ? fullName : owner.Username;

        return new DesktopSalesReviewSchedule(
            Flag(rows, WeeklyEnabled),
            Flag(rows, MonthlyEnabled),
            Addresses(rows.GetValueOrDefault(Recipients).Value),
            sendAs,
            sendAsName,
            rows.Values.Max(row => row.UpdatedAt),
            Date(rows, LastSent(DesktopSalesReviewCadence.Weekly)),
            Date(rows, LastSent(DesktopSalesReviewCadence.Monthly)));
    }
}
