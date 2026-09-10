using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Keeps a local copy of the VAT group each item is sold under, so a sale can be taxed correctly
/// without a SAP read in front of a waiting customer.
/// </summary>
/// <remarks>
/// The item master is the only place that answers what an item's tax is. Reading it costs a paged
/// sweep of every valid item against a SAP concurrency limit shared with everything else the process
/// does, which is not a price a till sale can pay — the same reasoning
/// <see cref="SapItemUomWarmJob"/> is written down for.
///
/// <para>
/// Runs nightly because that is how often the answer changes: an item's VAT group moves when the
/// tax law does, or when somebody corrects a master-data mistake. A day stale is a day of charging
/// what was correct yesterday; a sale taxed from no copy at all is charged the standard rate on
/// zero-rated goods, which is what this exists to stop.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed class SapItemTaxGroupWarmJob(
    IServiceScopeFactory scopeFactory,
    IOptions<TaxSettings> taxSettings,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<SapItemTaxGroupWarmJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();

        Dictionary<string, string> vatGroupsByItem;

        try
        {
            vatGroupsByItem = await sapClient.GetItemVatGroupsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Left exactly as it was. A stale copy taxes yesterday's way; an emptied one taxes
            // everything at the standard rate, which is the fault this job exists to prevent.
            logger.LogError(ex, "Could not read item VAT groups from SAP; keeping the copy we have.");
            return;
        }

        var result = await ApplyAsync(dbContext, vatGroupsByItem, DateTime.UtcNow, logger, cancellationToken);

        logger.LogInformation(
            "Item VAT groups warmed: {Total} read, {Added} new, {Changed} changed.",
            vatGroupsByItem.Count, result.Added, result.Changed);

        ReportUnconfiguredGroups(vatGroupsByItem);
    }

    /// <summary>
    /// Names any VAT group real items are on that the configuration does not describe.
    /// </summary>
    /// <remarks>
    /// Both fallbacks are silent and both are the standard rate: an unlisted group is charged
    /// <see cref="TaxSettings.VatRate"/>, and an unmapped one is declared under
    /// <see cref="RevmaxSettings.DefaultTaxId"/>. That was harmless while every line was
    /// standard-rated anyway. Now that a line is taxed at its item's own group, a group nobody
    /// configured is a group being charged and declared as something it is not — and the only way
    /// to find out used to be a customer noticing.
    ///
    /// <para>
    /// Driven by what the item master actually returns rather than by the config, so it stays quiet
    /// about codes that exist in SAP and are on nothing sellable.
    /// </para>
    /// </remarks>
    private void ReportUnconfiguredGroups(IReadOnlyDictionary<string, string> vatGroupsByItem)
    {
        var itemsPerGroup = vatGroupsByItem
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var tax = taxSettings.Value;
        var revmax = revmaxSettings.Value;

        foreach (var (group, itemCount) in itemsPerGroup.OrderByDescending(g => g.Value))
        {
            var hasRate = tax.RatesByTaxCode.ContainsKey(group);
            var hasTaxId = revmax.TaxIdMappings.ContainsKey(group);

            if (hasRate && hasTaxId)
            {
                continue;
            }

            if (!hasRate)
            {
                logger.LogError(
                    "VAT group {Group} is on {Count} sellable item(s) but has no rate in "
                    + "Tax:RatesByTaxCode, so those lines are charged the standard {Rate:P1}.",
                    group, itemCount, tax.VatRate);
            }

            if (!hasTaxId)
            {
                // Separate from the rate, because the damaging case is having one and not the other:
                // a zero-rated group with a rate and no tax id charges the customer nothing and tells
                // ZIMRA the standard rate was applied.
                logger.LogError(
                    "VAT group {Group} is on {Count} sellable item(s) but has no FDMS tax id in "
                    + "Revmax:TaxIdMappings, so those lines are declared as tax id {DefaultTaxId}.",
                    group, itemCount, revmax.DefaultTaxId);
            }
        }
    }

    /// <summary>
    /// Merges what SAP answered into the stored copy.
    /// </summary>
    /// <remarks>
    /// Separate from the read so the merge can be tested without standing up a SAP client. Every
    /// rule here is about not making things worse than having no copy at all.
    /// </remarks>
    internal static async Task<WarmOutcome> ApplyAsync(
        ApplicationDbContext dbContext,
        IReadOnlyDictionary<string, string> vatGroupsByItem,
        DateTime now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // An empty answer is not "every item lost its tax group". Taking it literally would empty
        // the table and put every till back on the standard rate for everything - the exact fault
        // this job exists to prevent, caused by the job meant to prevent it.
        if (vatGroupsByItem.Count == 0)
        {
            logger.LogWarning(
                "SAP returned no item VAT groups. Keeping the {Count} already stored.",
                await dbContext.SapItemTaxGroups.CountAsync(cancellationToken));
            return new WarmOutcome(0, 0);
        }

        var existing = await dbContext.SapItemTaxGroups
            .ToDictionaryAsync(row => row.ItemCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var added = 0;
        var changed = 0;

        foreach (var (itemCode, vatGroup) in vatGroupsByItem)
        {
            if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(vatGroup))
            {
                continue;
            }

            var code = itemCode.Trim();
            var group = vatGroup.Trim();

            if (existing.TryGetValue(code, out var row))
            {
                if (!string.Equals(row.VatGroup, group, StringComparison.OrdinalIgnoreCase))
                {
                    // Worth a line of its own: an item changing VAT group changes what every till
                    // charges for it from this point, and it is the kind of master-data edit nobody
                    // announces.
                    logger.LogInformation(
                        "Item {ItemCode} moved from VAT group {Was} to {Now}.",
                        code, row.VatGroup, group);

                    row.VatGroup = group;
                    changed++;
                }

                row.ResolvedAtUtc = now;
                continue;
            }

            dbContext.SapItemTaxGroups.Add(new SapItemTaxGroupEntity
            {
                ItemCode = code,
                VatGroup = group,
                ResolvedAtUtc = now
            });
            added++;
        }

        // Rows for items SAP no longer returns are left alone rather than deleted. A delisted item
        // is not sellable anyway, and an item missing from one read because the sweep was cut short
        // would otherwise lose its tax group and be sold at the standard rate.
        await dbContext.SaveChangesAsync(cancellationToken);

        return new WarmOutcome(added, changed);
    }

    internal readonly record struct WarmOutcome(int Added, int Changed);
}
