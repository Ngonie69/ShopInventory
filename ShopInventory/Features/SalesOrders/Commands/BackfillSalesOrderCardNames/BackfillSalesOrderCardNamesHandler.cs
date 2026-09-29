using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.BackfillSalesOrderCardNames;

/// <summary>
/// Gives sales orders stored without a customer name the name SAP holds for their card code.
/// </summary>
/// <remarks>
/// <para>
/// One batched read for every code that needs a name, not one read per code: this used to call
/// <c>GetBusinessPartnerByCodeAsync</c> in a loop, in series, which is the pattern the SAP client
/// warns against.
/// </para>
/// <para>
/// A code SAP does not know is remembered (<see cref="UnresolvedConfigKey"/>) and not asked about
/// again for <see cref="UnresolvedRetryAfter"/>; it used to be asked about on every start. A SAP
/// failure is not a missing code: the read throws, nothing is remembered, and the next run tries
/// again. It used to go through the business-partner service, which answers a SAP failure with null,
/// so a failure and a missing customer looked the same.
/// </para>
/// </remarks>
public sealed class BackfillSalesOrderCardNamesHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<BackfillSalesOrderCardNamesHandler> logger
) : IRequestHandler<BackfillSalesOrderCardNamesCommand, ErrorOr<BackfillSalesOrderCardNamesResult>>
{
    internal const string UnresolvedConfigKey = "SalesOrderCardNameBackfill.Unresolved";
    internal static readonly TimeSpan UnresolvedRetryAfter = TimeSpan.FromDays(7);

    public async Task<ErrorOr<BackfillSalesOrderCardNamesResult>> Handle(
        BackfillSalesOrderCardNamesCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var orders = await context.SalesOrders
                .AsTracking()
                .Where(order => order.CardCode != null &&
                    (order.CardName == null || order.CardName == string.Empty || order.CardName == order.CardCode))
                .ToListAsync(cancellationToken);

            if (orders.Count == 0)
            {
                return new BackfillSalesOrderCardNamesResult(0, 0, 0);
            }

            var now = DateTime.UtcNow;
            var unresolvedRow = await context.SystemConfigs
                .AsTracking()
                .FirstOrDefaultAsync(config => config.Key == UnresolvedConfigKey, cancellationToken);
            var unresolved = ReadUnresolved(unresolvedRow?.Value);

            var ordersByCode = orders
                .Where(order => !string.IsNullOrWhiteSpace(order.CardCode))
                .GroupBy(order => order.CardCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var toAsk = ordersByCode.Keys
                .Where(code => !unresolved.TryGetValue(code, out var triedAt) || now - triedAt >= UnresolvedRetryAfter)
                .ToList();

            if (toAsk.Count == 0)
            {
                return new BackfillSalesOrderCardNamesResult(0, 0, 0);
            }

            var names = (await sapClient.GetBusinessPartnersByCodesAsync(toAsk, cancellationToken))
                .Where(partner => !string.IsNullOrWhiteSpace(partner.CardCode))
                .GroupBy(partner => partner.CardCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().CardName?.Trim(), StringComparer.OrdinalIgnoreCase);

            var ordersUpdated = 0;
            var customersResolved = 0;
            var customersUnresolved = 0;

            foreach (var code in toAsk)
            {
                if (!names.TryGetValue(code, out var name) ||
                    string.IsNullOrWhiteSpace(name) ||
                    string.Equals(name, code, StringComparison.OrdinalIgnoreCase))
                {
                    unresolved[code] = now;
                    customersUnresolved++;
                    continue;
                }

                unresolved.Remove(code);
                customersResolved++;

                foreach (var order in ordersByCode[code])
                {
                    order.CardName = name;
                    order.UpdatedAt = now;
                    ordersUpdated++;
                }
            }

            SaveUnresolved(unresolvedRow, unresolved, now);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Backfilled sales order customer names for {OrdersUpdated} orders across {CustomersResolved} customers; {CustomersUnresolved} customers could not be resolved and are not asked about again for {RetryDays} days",
                ordersUpdated,
                customersResolved,
                customersUnresolved,
                UnresolvedRetryAfter.TotalDays);

            return new BackfillSalesOrderCardNamesResult(ordersUpdated, customersResolved, customersUnresolved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill sales order customer names");
            return Errors.SalesOrder.BackfillFailed(ex.GetBaseException().Message);
        }
    }

    private static Dictionary<string, DateTime> ReadUnresolved(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
            return stored is null
                ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, DateTime>(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Unreadable memory is only a missed saving: every code is asked about again.
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnresolved(SystemConfigEntity? row, Dictionary<string, DateTime> unresolved, DateTime now)
    {
        var value = JsonSerializer.Serialize(unresolved);
        if (row is null)
        {
            if (unresolved.Count == 0)
            {
                return;
            }

            context.SystemConfigs.Add(new SystemConfigEntity
            {
                Key = UnresolvedConfigKey,
                Value = value,
                ValueType = "json",
                Category = "Synchronization",
                Description = "Sales order customer codes SAP did not know, and when each was last asked about.",
                IsEditable = false,
                UpdatedAt = now
            });
            return;
        }

        if (row.Value != value)
        {
            row.Value = value;
            row.UpdatedAt = now;
        }
    }
}
