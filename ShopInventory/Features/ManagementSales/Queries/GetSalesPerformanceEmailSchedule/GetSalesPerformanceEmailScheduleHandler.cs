using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using static ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewSchedule.DesktopSalesReviewScheduleKeys;
using Keys = ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule.SalesPerformanceEmailScheduleKeys;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;

public sealed class GetSalesPerformanceEmailScheduleHandler(ApplicationDbContext db)
    : IRequestHandler<GetSalesPerformanceEmailScheduleQuery, ErrorOr<SalesPerformanceEmailSchedule>>
{
    public async Task<ErrorOr<SalesPerformanceEmailSchedule>> Handle(
        GetSalesPerformanceEmailScheduleQuery request, CancellationToken cancellationToken)
    {
        var rows = await Keys.ReadAsync(db, cancellationToken);

        Guid? sendAs = rows.TryGetValue(Keys.SendAsUserId, out var stored) && Guid.TryParse(stored.Value, out var id) ? id : null;
        var owner = sendAs is null
            ? null
            : await db.Users.AsNoTracking()
                .Where(user => user.Id == sendAs)
                .Select(user => new { user.Username, user.FirstName, user.LastName })
                .FirstOrDefaultAsync(cancellationToken);
        var fullName = $"{owner?.FirstName} {owner?.LastName}".Trim();
        var sendAsName = owner is null ? null : fullName.Length > 0 ? fullName : owner.Username;

        return new SalesPerformanceEmailSchedule(
            Flag(rows, Keys.Enabled(SalesPerformanceEmailCadence.Daily)),
            Flag(rows, Keys.Enabled(SalesPerformanceEmailCadence.Weekly)),
            Flag(rows, Keys.Enabled(SalesPerformanceEmailCadence.Monthly)),
            Addresses(rows.GetValueOrDefault(Keys.Recipients).Value),
            sendAs,
            sendAsName,
            rows.Count == 0 ? null : rows.Values.Max(row => row.UpdatedAt),
            Date(rows, Keys.LastSent(SalesPerformanceEmailCadence.Daily)),
            Date(rows, Keys.LastSent(SalesPerformanceEmailCadence.Weekly)),
            Date(rows, Keys.LastSent(SalesPerformanceEmailCadence.Monthly)));
    }
}
