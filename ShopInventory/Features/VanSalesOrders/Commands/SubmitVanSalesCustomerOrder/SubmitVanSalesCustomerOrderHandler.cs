using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders.Commands.SubmitVanSalesCustomerOrder;

/// <summary>
/// Takes a customer's order, prices it, and accepts it.
/// </summary>
/// <remarks>
/// Idempotent on <c>ClientRequestId</c>, in two layers. The lookup at the top answers the ordinary
/// retry cheaply; the unique index behind it closes the race the lookup cannot, where two copies of
/// the same queued order arrive together and both find nothing. On that collision the order that
/// won is returned as though this call had created it — because from the handset's point of view it
/// did, and reporting a conflict would leave the app believing it must try again.
/// <para>
/// Prices are resolved here and the handset's are ignored. It caches a catalogue that can be days
/// old, and a customer whose app quoted last week's price is charged this week's — so the price
/// comes back on the response for the app to show rather than being taken from the request.
/// </para>
/// <para>
/// Availability is deliberately <em>not</em> checked. The decision was auto-accept with the rep
/// adjusting at delivery; refusing an order for an out-of-stock line would reject demand the depot
/// may well restock before the van is loaded, and lose the signal that it was wanted.
/// </para>
/// </remarks>
public sealed class SubmitVanSalesCustomerOrderHandler(
    ApplicationDbContext context,
    IVanSalesCatalogueReader catalogueReader,
    IVanSalesOrderingPolicy orderingPolicy,
    IAuditService auditService,
    ILogger<SubmitVanSalesCustomerOrderHandler> logger)
    : IRequestHandler<SubmitVanSalesCustomerOrderCommand, ErrorOr<VanSalesOrderResult>>
{
    /// <summary>
    /// Attempts at a free order number before giving up.
    /// </summary>
    /// <remarks>
    /// The number is derived from the highest one issued today, so two orders submitted in the same
    /// instant can pick the same one. The unique index catches it and the retry picks the next.
    /// Three is generous: a collision needs simultaneous submissions, and two in a row needs them
    /// three deep.
    /// </remarks>
    private const int OrderNumberAttempts = 3;

    /// <summary>A requested item after duplicates of it have been folded together.</summary>
    private sealed record RequestedLine(string ItemCode, decimal Quantity);

    public async Task<ErrorOr<VanSalesOrderResult>> Handle(
        SubmitVanSalesCustomerOrderCommand command,
        CancellationToken cancellationToken)
    {
        var clientRequestId = command.ClientRequestId!.Trim();

        var existing = await FindByClientRequestIdAsync(clientRequestId, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation(
                "Van sales customer order {OrderNumber} replayed for client request {ClientRequestId}; returning the original.",
                existing.OrderNumber,
                clientRequestId);
            return existing;
        }

        var account = await context.VanSalesCustomerAccounts
            .AsNoTracking()
            .Where(a => a.Id == command.AccountId && a.IsActive && a.RouteCustomer != null)
            .Select(a => new
            {
                a.Id,
                a.RouteCustomerId,
                Code = a.RouteCustomer!.Code,
                Name = a.RouteCustomer.Name,
                a.RouteCustomer.AssignedBusinessPartnerCode,
                CustomerActive = a.RouteCustomer.IsActive
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (account is null || !account.CustomerActive)
        {
            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        var rules = await orderingPolicy.GetRulesAsync(cancellationToken);

        var visitDays = await context.RouteCustomerVisitDays
            .AsNoTracking()
            .Where(d => d.RouteCustomerId == account.RouteCustomerId)
            .Select(d => d.DayOfWeek)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;

        var visitDate = ResolveVisitDate(command.RequestedVisitDate, now, visitDays, rules);
        if (visitDate.IsError)
        {
            return visitDate.Errors;
        }

        var catalogue = await catalogueReader.ReadAsync(cancellationToken);

        // Lines are collapsed by item first. A shopkeeper who adds the same product twice means one
        // larger quantity, not two lines the picker has to reconcile at the depot.
        var requested = command.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemCode))
            .GroupBy(l => l.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RequestedLine(g.Key, g.Sum(l => l.Quantity)))
            .ToList();

        if (requested.Count == 0)
        {
            return Errors.VanSalesOrders.NoLines;
        }

        var unavailable = requested
            .Where(l => !catalogue.ItemsByCode.ContainsKey(l.ItemCode))
            .Select(l => l.ItemCode)
            .ToList();

        if (unavailable.Count > 0)
        {
            logger.LogInformation(
                "Refused a van sales customer order for {Customer}: {Items} are not on the catalogue.",
                account.Code,
                string.Join(", ", unavailable));

            return Errors.VanSalesOrders.UnavailableItems(unavailable);
        }

        var route = await ResolveRouteAsync(account.AssignedBusinessPartnerCode, cancellationToken);

        var order = BuildOrder(command, account.Id, account.RouteCustomerId, account.Code, account.Name,
            account.AssignedBusinessPartnerCode, route, visitDate.Value, catalogue, requested, clientRequestId, now);

        var saved = await SaveWithIdempotencyAsync(order, clientRequestId, cancellationToken);
        if (saved.IsError)
        {
            return saved.Errors;
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.SubmitVanSalesCustomerOrder,
                "VanSalesOrder",
                saved.Value.Id.ToString(),
                $"Order {saved.Value.OrderNumber} placed by {account.Code} for {saved.Value.Lines.Count} item(s), total {saved.Value.DocTotal:0.00}.",
                true);
        }
        catch
        {
            // Auditing must not cost a customer the order they just placed; the surrounding
            // handlers treat it the same way.
        }

        return saved;
    }

    /// <summary>
    /// The call the order is for, and whether it may still be ordered into.
    /// </summary>
    /// <remarks>
    /// A date the handset names is checked against the schedule rather than trusted: a queued order
    /// can arrive days after the call it asked for, and accepting it would put stock on a van that
    /// left last week. With no date named, the next open call is chosen here — which is also what a
    /// shop with no calling days gets, as null, meaning the next available run.
    /// </remarks>
    private static ErrorOr<DateTime?> ResolveVisitDate(
        DateTime? requested,
        DateTime nowUtc,
        IReadOnlyCollection<DayOfWeek> visitDays,
        VanSalesOrderingRules rules)
    {
        if (requested is { } named)
        {
            var date = named.Date;

            return VanSalesVisitSchedule.IsOpenForVisitDate(
                nowUtc, date, visitDays, rules.CutOffHoursBeforeVisitDay)
                ? date
                : Errors.VanSalesOrders.OrderingClosed;
        }

        var window = VanSalesVisitSchedule.NextOpenVisit(nowUtc, visitDays, rules.CutOffHoursBeforeVisitDay);

        return window.IsOrderingOpen
            ? window.NextVisitDate
            : Errors.VanSalesOrders.OrderingClosed;
    }

    private async Task<(string? Code, string? Name)> ResolveRouteAsync(
        string businessPartnerCode,
        CancellationToken cancellationToken)
    {
        var route = await context.Users
            .AsNoTracking()
            .Where(u => u.AssignedBusinessPartnerCode == businessPartnerCode
                        && u.RouteId != null
                        && u.Route != null)
            .Select(u => new { u.Route!.Code, u.Route.Name })
            .FirstOrDefaultAsync(cancellationToken);

        return (route?.Code, route?.Name);
    }

    private static VanSalesOrderEntity BuildOrder(
        SubmitVanSalesCustomerOrderCommand command,
        int accountId,
        int routeCustomerId,
        string customerCode,
        string customerName,
        string businessPartnerCode,
        (string? Code, string? Name) route,
        DateTime? visitDate,
        VanSalesPricedCatalogue catalogue,
        IReadOnlyList<RequestedLine> requested,
        string clientRequestId,
        DateTime now)
    {
        var order = new VanSalesOrderEntity
        {
            VanSalesCustomerAccountId = accountId,
            RouteCustomerId = routeCustomerId,
            RouteCustomerCode = customerCode,
            RouteCustomerName = customerName,
            AssignedBusinessPartnerCode = businessPartnerCode,
            RouteCode = route.Code,
            RouteName = route.Name,
            RequestedVisitDate = visitDate,
            Status = VanSalesOrderStatus.Accepted,
            Currency = catalogue.Currency,
            ClientRequestId = clientRequestId,
            CustomerNotes = command.CustomerNotes,
            SubmittedAtUtc = command.SubmittedAtUtc,
            ReceivedAtUtc = now,
            DeviceInfo = command.DeviceInfo,
            AppVersion = command.AppVersion,
            Latitude = command.Latitude,
            Longitude = command.Longitude,
            CreatedAt = now,
            UpdatedAt = now
        };

        var lineNumber = 1;

        foreach (var line in requested)
        {
            var item = catalogue.ItemsByCode[line.ItemCode];

            // Rounded per line, then summed. Rounding the total instead would let a basket of many
            // small lines drift a cent or two from the sum a customer can do on the invoice.
            var lineTotal = Math.Round(item.UnitPrice * line.Quantity, 2, MidpointRounding.AwayFromZero);

            order.Lines.Add(new VanSalesOrderLineEntity
            {
                LineNumber = lineNumber++,
                ItemCode = item.ItemCode,
                ItemDescription = item.ItemName,
                UoMCode = item.UnitOfMeasure,
                QuantityOrdered = line.Quantity,
                QuantityFulfilled = 0m,
                UnitPrice = item.UnitPrice,
                TaxPercent = item.TaxPercent,
                LineTotal = lineTotal
            });
        }

        order.SubTotal = order.Lines.Sum(l => l.LineTotal);
        order.TaxAmount = order.Lines.Sum(l =>
            Math.Round(l.LineTotal * l.TaxPercent / 100m, 2, MidpointRounding.AwayFromZero));
        order.DocTotal = order.SubTotal + order.TaxAmount;

        return order;
    }

    /// <summary>
    /// Persists the order, treating a duplicate key as a successful replay.
    /// </summary>
    private async Task<ErrorOr<VanSalesOrderResult>> SaveWithIdempotencyAsync(
        VanSalesOrderEntity order,
        string clientRequestId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= OrderNumberAttempts; attempt++)
        {
            order.OrderNumber = await GenerateOrderNumberAsync(cancellationToken);
            context.VanSalesOrders.Add(order);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return VanSalesOrderProjection.ToResultInMemory(order);
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex, "ClientRequestId"))
            {
                // Two copies of the same queued order arrived together and the other won. Its
                // result is the right answer to both.
                context.ChangeTracker.Clear();

                var winner = await FindByClientRequestIdAsync(clientRequestId, CancellationToken.None);
                if (winner is not null)
                {
                    logger.LogInformation(
                        "Van sales customer order for client request {ClientRequestId} was created concurrently; returning {OrderNumber}.",
                        clientRequestId,
                        winner.OrderNumber);
                    return winner;
                }

                logger.LogError(
                    "Client request {ClientRequestId} collided on insert but no order could be read back.",
                    clientRequestId);
                throw;
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex, "OrderNumber") && attempt < OrderNumberAttempts)
            {
                // Someone else took the number between generating it and inserting. Detach and try
                // the next one; the order itself is untouched.
                context.ChangeTracker.Clear();
                logger.LogInformation(
                    "Van sales order number {OrderNumber} was taken; retrying (attempt {Attempt}).",
                    order.OrderNumber,
                    attempt);
            }
        }

        logger.LogError(
            "Could not find a free van sales order number after {Attempts} attempts for client request {ClientRequestId}.",
            OrderNumberAttempts,
            clientRequestId);

        return Error.Failure(
            "VanSalesOrders.OrderNumberUnavailable",
            "The order could not be saved. Please try again.");
    }

    private async Task<VanSalesOrderResult?> FindByClientRequestIdAsync(
        string clientRequestId,
        CancellationToken cancellationToken)
        => await context.VanSalesOrders
            .AsNoTracking()
            .Where(o => o.ClientRequestId == clientRequestId)
            .Select(VanSalesOrderProjection.ToResult)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The next number in today's series: <c>VSO-20260824-0001</c>.
    /// </summary>
    /// <remarks>
    /// Ordered by length before value so that <c>0010</c> sorts after <c>0009</c> rather than
    /// between <c>0001</c> and <c>0002</c> — the same trap, and the same fix, as
    /// <c>SalesOrderService.GenerateOrderNumberAsync</c>.
    /// </remarks>
    private async Task<string> GenerateOrderNumberAsync(CancellationToken cancellationToken)
    {
        var prefix = $"VSO-{DateTime.UtcNow:yyyyMMdd}-";

        var last = await context.VanSalesOrders
            .AsNoTracking()
            .Where(o => o.OrderNumber.StartsWith(prefix))
            .OrderByDescending(o => o.OrderNumber.Length)
            .ThenByDescending(o => o.OrderNumber)
            .Select(o => o.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1L;
        if (last is not null && long.TryParse(last[prefix.Length..], out var parsed))
        {
            sequence = parsed + 1;
        }

        return $"{prefix}{sequence:D4}";
    }

    /// <summary>
    /// Whether a save failed on a unique index covering <paramref name="column"/>.
    /// </summary>
    /// <remarks>
    /// Matches on the constraint name, as <c>SalesOrderService</c> does. SQLite — which the unit
    /// tests run on — reports the same violation differently, so the message is checked too;
    /// without that the idempotency path would be untestable outside a real PostgreSQL.
    /// </remarks>
    private static bool IsDuplicate(DbUpdateException exception, string column)
    {
        if (exception.InnerException is PostgresException postgres)
        {
            return postgres.SqlState == PostgresErrorCodes.UniqueViolation
                   && postgres.ConstraintName?.Contains(column, StringComparison.OrdinalIgnoreCase) == true;
        }

        var message = exception.InnerException?.Message;
        return message is not null
               && message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               && message.Contains(column, StringComparison.OrdinalIgnoreCase);
    }
}
