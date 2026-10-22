using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.RouteCustomers.Commands.ImportRouteSchedule;

/// <summary>
/// Matches the workbook's outlets to each truck's shops and, unless only checking, makes the result
/// the truck's calling schedule.
/// </summary>
/// <remarks>
/// <para>
/// <b>A truck is a van account.</b> Shops are kept under the account the reps sell on
/// (<see cref="RouteCustomerEntity.AssignedBusinessPartnerCode"/>), so a sheet has to be tied to one.
/// The page says which; when it does not, the sheet name is matched to a route ("WEST 1" to WEST1,
/// "CZA" and "CBD" both to CBDCZA) and the route to the account most of its reps sell on. Two sheets
/// may name the same truck, and then they make one schedule between them.
/// </para>
/// <para>
/// <b>Saving replaces the truck's schedule.</b> Every calling day on every shop of the truck is
/// removed and the matched days written, in one transaction. A shop the workbook does not name is
/// left with no day — the workbook is the plan. A truck on which nothing matched is not touched at
/// all, because replacing its schedule with nothing would be a mistake made by a bad upload, not a
/// plan anybody wrote.
/// </para>
/// <para>
/// <b>History moves with it.</b> The reports build a past day's round from the schedule as it is
/// now, not as it was, so saving restates the CCR of days already reported. That is the right answer
/// for the first import — those days had no plan at all — and the reason the page says so.
/// </para>
/// </remarks>
public sealed class ImportRouteScheduleHandler(
    ApplicationDbContext context,
    ILogger<ImportRouteScheduleHandler> logger)
    : IRequestHandler<ImportRouteScheduleCommand, ErrorOr<RouteScheduleImportResultDto>>
{
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    public async Task<ErrorOr<RouteScheduleImportResultDto>> Handle(
        ImportRouteScheduleCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var vendingDepots = await RouteCustomerScopes.VendingDepotCodesAsync(
            context, RouteCustomerScope.Route, cancellationToken);

        var customers = (await context.RouteCustomers
                .AsNoTracking()
                .Select(customer => new
                {
                    customer.Id,
                    customer.Code,
                    customer.Name,
                    customer.AssignedBusinessPartnerCode,
                    customer.IsActive,
                })
                .ToListAsync(cancellationToken))
            .Where(customer => !string.IsNullOrWhiteSpace(customer.AssignedBusinessPartnerCode)
                               && RouteCustomerScopes.Includes(RouteCustomerScope.Route, vendingDepots, customer.AssignedBusinessPartnerCode))
            .Select(customer => new Customer(
                customer.Id,
                customer.Code,
                customer.Name,
                VanTruckRoutes.TruckOfAccount(customer.AssignedBusinessPartnerCode),
                customer.IsActive))
            .ToList();

        var routes = await context.Routes
            .AsNoTracking()
            .Where(route => route.DeletedAt == null)
            .Select(route => new RouteRow(route.Id, route.Code, route.Name))
            .ToListAsync(cancellationToken);

        var reps = await context.Users
            .AsNoTracking()
            .Where(user => user.RouteId != null
                           && user.AssignedBusinessPartnerCode != null
                           && user.AssignedBusinessPartnerCode != "")
            .Select(user => new { RouteId = user.RouteId!.Value, Account = user.AssignedBusinessPartnerCode! })
            .ToListAsync(cancellationToken);

        // A route's truck: the account most of its reps sell on. A truck's route: the reverse.
        var accountOfRoute = reps
            .GroupBy(rep => rep.RouteId)
            .ToDictionary(
                route => route.Key,
                route => route
                    .GroupBy(rep => VanTruckRoutes.TruckOfAccount(rep.Account))
                    .OrderByDescending(account => account.Count())
                    .ThenBy(account => account.Key, StringComparer.Ordinal)
                    .First().Key);

        var routeNames = routes.ToDictionary(route => route.Id, route => route.Name);
        var routeOfAccount = reps
            .GroupBy(rep => VanTruckRoutes.TruckOfAccount(rep.Account))
            .ToDictionary(
                account => account.Key,
                account => account
                    .GroupBy(rep => rep.RouteId)
                    .OrderByDescending(route => route.Count())
                    .ThenBy(route => route.Key)
                    .Select(route => routeNames.GetValueOrDefault(route.Key))
                    .First(),
                StringComparer.Ordinal);

        var activeByAccount = customers
            .Where(customer => customer.IsActive)
            .GroupBy(customer => customer.Account, StringComparer.Ordinal)
            .ToDictionary(account => account.Key, account => account.ToList(), StringComparer.Ordinal);

        var result = new RouteScheduleImportResultDto
        {
            Trucks = activeByAccount
                .Select(account => new RouteScheduleTruckOptionDto
                {
                    AccountCode = account.Key,
                    RouteName = routeOfAccount.GetValueOrDefault(account.Key),
                    ActiveShopCount = account.Value.Count,
                })
                .OrderBy(truck => truck.AccountCode, StringComparer.Ordinal)
                .ToList(),
        };

        var matchers = new Dictionary<string, RouteScheduleMatcher>(StringComparer.Ordinal);

        // Shop id -> the days it is called on, per truck.
        var plan = new Dictionary<string, Dictionary<int, HashSet<DayOfWeek>>>(StringComparer.Ordinal);

        foreach (var sheet in request.Sheets)
        {
            var account = string.IsNullOrWhiteSpace(sheet.AccountCode)
                ? GuessAccount(sheet.Name, routes, accountOfRoute)
                : VanTruckRoutes.TruckOfAccount(sheet.AccountCode);

            var sheetResult = new RouteScheduleSheetResultDto
            {
                Name = sheet.Name,
                AccountCode = account,
                AccountGuessed = account is not null && string.IsNullOrWhiteSpace(sheet.AccountCode),
            };
            result.Sheets.Add(sheetResult);

            var shops = account is null ? [] : activeByAccount.GetValueOrDefault(account) ?? [];
            var shopsById = shops.ToDictionary(shop => shop.Id);

            if (account is not null)
            {
                plan.TryAdd(account, []);

                if (!matchers.TryGetValue(account, out _))
                {
                    matchers[account] = new RouteScheduleMatcher(
                        shops.Select(shop => new RouteScheduleMatcher.Shop(shop.Id, shop.Code, shop.Name)));
                }
            }

            foreach (var outlet in sheet.Outlets)
            {
                var outletResult = new RouteScheduleOutletResultDto
                {
                    Ref = outlet.Ref,
                    Name = outlet.Name,
                    Day = outlet.Day,
                    Sequence = outlet.Sequence,
                };
                sheetResult.Outlets.Add(outletResult);

                if (account is null)
                {
                    outletResult.Status = RouteScheduleMatchStatus.NoTruck;
                    continue;
                }

                // The automatic match is worked out even when the office overruled it, so the page can
                // still offer the same choices on the next check.
                var match = matchers[account].Find(outlet.Name);
                outletResult.Candidates = match.Candidates.Select(ToCandidate).ToList();

                Customer? chosen = null;

                if (outlet.Excluded)
                {
                    outletResult.Status = RouteScheduleMatchStatus.Excluded;
                    AddMatchedAsCandidate(outletResult, match);
                }
                else if (outlet.PickedRouteCustomerId is { } picked)
                {
                    if (shopsById.TryGetValue(picked, out var pickedShop))
                    {
                        outletResult.Status = RouteScheduleMatchStatus.Picked;
                        chosen = pickedShop;
                        AddMatchedAsCandidate(outletResult, match);
                    }
                    else
                    {
                        outletResult.Status = RouteScheduleMatchStatus.PickRejected;
                    }
                }
                else
                {
                    outletResult.Status = match.Status;
                    chosen = match.Shop is { } shop ? shopsById[shop.Id] : null;

                    // A close match is saved but can be overruled, so it is offered as a choice too.
                    if (match.Status == RouteScheduleMatchStatus.Close)
                    {
                        AddMatchedAsCandidate(outletResult, match);
                    }
                }

                if (chosen is not null)
                {
                    outletResult.RouteCustomerId = chosen.Id;
                    outletResult.RouteCustomerCode = chosen.Code;
                    outletResult.RouteCustomerName = chosen.Name;

                    var days = plan[account];
                    if (!days.TryGetValue(chosen.Id, out var set))
                    {
                        days[chosen.Id] = set = [];
                    }

                    set.Add(outlet.Day);
                }
            }
        }

        var accountIds = plan.Keys.ToList();
        var scheduledBefore = await ScheduledShopsAsync(accountIds, customers, cancellationToken);

        foreach (var (account, days) in plan.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            result.Accounts.Add(new RouteScheduleAccountSummaryDto
            {
                AccountCode = account,
                RouteName = routeOfAccount.GetValueOrDefault(account),
                ActiveShopCount = activeByAccount.GetValueOrDefault(account)?.Count ?? 0,
                ScheduledBefore = scheduledBefore.GetValueOrDefault(account),
                ScheduledAfter = days.Count,
                ShopsPerDay = WeekOrder
                    .Select(day => new RouteScheduleDayCountDto { Day = day, Shops = days.Values.Count(set => set.Contains(day)) })
                    .Where(count => count.Shops > 0)
                    .ToList(),
                Note = days.Count == 0
                    ? "Nothing on the sheet matched a shop on this truck, so its schedule is left as it is."
                    : null,
            });
        }

        if (request.ValidateOnly)
        {
            return result;
        }

        var toSave = result.Accounts.Where(summary => summary.ScheduledAfter > 0).ToList();
        if (toSave.Count == 0)
        {
            return result;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var summary in toSave)
        {
            // Every shop of the truck, removed ones included: a removed shop's old days are not part
            // of the new plan either.
            var truckShopIds = customers
                .Where(customer => customer.Account == summary.AccountCode)
                .Select(customer => customer.Id)
                .ToList();

            await context.RouteCustomerVisitDays
                .Where(visitDay => truckShopIds.Contains(visitDay.RouteCustomerId))
                .ExecuteDeleteAsync(cancellationToken);

            var now = DateTime.UtcNow;
            context.RouteCustomerVisitDays.AddRange(plan[summary.AccountCode]
                .SelectMany(shop => shop.Value.Select(day => new RouteCustomerVisitDayEntity
                {
                    RouteCustomerId = shop.Key,
                    DayOfWeek = day,
                    CreatedAt = now,
                })));

            summary.Applied = true;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        result.Applied = true;

        foreach (var summary in toSave)
        {
            logger.LogInformation(
                "User {UserId} replaced the calling schedule of {Account}: {Before} shops scheduled before, {After} after ({Days})",
                command.UserId,
                summary.AccountCode,
                summary.ScheduledBefore,
                summary.ScheduledAfter,
                string.Join(", ", summary.ShopsPerDay.Select(day => $"{day.Day} {day.Shops}")));
        }

        return result;
    }

    /// <summary>
    /// The truck a sheet is for, from its name: the one route whose code or name is the sheet's name,
    /// or failing that the one that contains it — and then the account that route's reps sell on.
    /// </summary>
    internal static string? GuessAccount(
        string sheetName,
        IReadOnlyList<RouteRow> routes,
        IReadOnlyDictionary<int, string> accountOfRoute)
    {
        var sheet = VanDayPlans.AreaKey(sheetName);
        if (sheet.Length == 0)
        {
            return null;
        }

        var keyed = routes
            .Select(route => (route.Id, Code: VanDayPlans.AreaKey(route.Code), Name: VanDayPlans.AreaKey(route.Name)))
            .ToList();

        var exact = keyed.Where(route => route.Code == sheet || route.Name == sheet).ToList();
        var found = exact.Count > 0
            ? exact
            : keyed.Where(route => route.Code.Contains(sheet, StringComparison.Ordinal)
                                   || route.Name.Contains(sheet, StringComparison.Ordinal)).ToList();

        return found.Count == 1 && accountOfRoute.TryGetValue(found[0].Id, out var account) ? account : null;
    }

    private async Task<Dictionary<string, int>> ScheduledShopsAsync(
        IReadOnlyCollection<string> accounts,
        IReadOnlyList<Customer> customers,
        CancellationToken cancellationToken)
    {
        var active = customers
            .Where(customer => customer.IsActive && accounts.Contains(customer.Account))
            .ToDictionary(customer => customer.Id, customer => customer.Account);

        if (active.Count == 0)
        {
            return [];
        }

        var ids = active.Keys.ToList();
        var scheduled = await context.RouteCustomerVisitDays
            .AsNoTracking()
            .Where(visitDay => ids.Contains(visitDay.RouteCustomerId))
            .Select(visitDay => visitDay.RouteCustomerId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return scheduled
            .GroupBy(id => active[id], StringComparer.Ordinal)
            .ToDictionary(account => account.Key, account => account.Count(), StringComparer.Ordinal);
    }

    private static void AddMatchedAsCandidate(RouteScheduleOutletResultDto outlet, RouteScheduleMatcher.Match match)
    {
        if (match.Shop is { } shop && outlet.Candidates.All(candidate => candidate.RouteCustomerId != shop.Id))
        {
            outlet.Candidates.Insert(0, ToCandidate(shop));
        }
    }

    private static RouteScheduleCandidateDto ToCandidate(RouteScheduleMatcher.Shop shop) => new()
    {
        RouteCustomerId = shop.Id,
        Code = shop.Code,
        Name = shop.Name,
    };

    private sealed record Customer(int Id, string Code, string Name, string Account, bool IsActive);

    internal sealed record RouteRow(int Id, string Code, string Name);
}
