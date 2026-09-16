using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Commands.SyncItemTaxGroups;

public sealed class SyncItemTaxGroupsHandler(
    ISAPServiceLayerClient sapClient,
    ApplicationDbContext context,
    IOptions<TaxSettings> taxSettings,
    IOptions<RevmaxSettings> revmaxSettings,
    BackgroundWorkerLeaderElector leaderElector,
    ILogger<SyncItemTaxGroupsHandler> logger
) : IRequestHandler<SyncItemTaxGroupsCommand, ErrorOr<ItemTaxGroupSyncResult>>
{
    private const string ClusterLockName = "item-tax-group-sync-run";

    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    public async Task<ErrorOr<ItemTaxGroupSyncResult>> Handle(
        SyncItemTaxGroupsCommand command,
        CancellationToken cancellationToken)
    {
        // Two passes at once would both insert the same new items, and the unique index on ItemCode
        // would fail whichever saved second. The in-process gate covers the nightly job meeting a
        // button press on one node; the advisory lock covers two nodes.
        if (!await SyncLock.WaitAsync(0, cancellationToken))
        {
            return Errors.Sync.ItemTaxGroupSyncAlreadyRunning;
        }

        try
        {
            await using var clusterLease = await leaderElector.TryAcquireAsync(ClusterLockName, cancellationToken);
            if (clusterLease is null)
            {
                return Errors.Sync.ItemTaxGroupSyncAlreadyRunning;
            }

            Dictionary<string, string> vatGroupsByItem;

            try
            {
                // Never the cached read: it can be six hours old, so a sync run because somebody just
                // changed an item in SAP would copy the group they changed it from.
                vatGroupsByItem = await sapClient.RefreshItemVatGroupsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left exactly as it was. A stale copy taxes yesterday's way; an emptied one taxes
                // everything at the standard rate, which is the fault this table exists to prevent.
                logger.LogError(ex, "Could not read item VAT groups from SAP; keeping the copy we have.");
                return Errors.Sync.ItemTaxGroupReadFailed(ex.Message);
            }

            var now = DateTime.UtcNow;
            var outcome = await ApplyAsync(context, vatGroupsByItem, now, logger, cancellationToken);

            if (vatGroupsByItem.Count == 0)
            {
                return Errors.Sync.ItemTaxGroupReadFailed("SAP answered with no items.");
            }

            logger.LogInformation(
                "Item VAT groups synced: {Total} read, {Added} new, {Changed} changed.",
                vatGroupsByItem.Count, outcome.Added, outcome.Changed.Count);

            return new ItemTaxGroupSyncResult(
                vatGroupsByItem.Count,
                outcome.Added,
                outcome.Changed,
                ReportUnconfiguredGroups(vatGroupsByItem),
                now);
        }
        finally
        {
            SyncLock.Release();
        }
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
    private List<string> ReportUnconfiguredGroups(IReadOnlyDictionary<string, string> vatGroupsByItem)
    {
        var itemsPerGroup = vatGroupsByItem
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .GroupBy(pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var tax = taxSettings.Value;
        var revmax = revmaxSettings.Value;
        var unconfigured = new List<string>();

        foreach (var (group, itemCount) in itemsPerGroup.OrderByDescending(g => g.Value))
        {
            var hasRate = tax.RatesByTaxCode.ContainsKey(group);
            var hasTaxId = revmax.TaxIdMappings.ContainsKey(group);

            if (hasRate && hasTaxId)
            {
                continue;
            }

            unconfigured.Add(group);

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

        return unconfigured;
    }

    /// <summary>
    /// Merges what SAP answered into the stored copy.
    /// </summary>
    /// <remarks>
    /// Separate from the read so the merge can be tested without standing up a SAP client. Every
    /// rule here is about not making things worse than having no copy at all.
    /// </remarks>
    internal static async Task<ApplyOutcome> ApplyAsync(
        ApplicationDbContext dbContext,
        IReadOnlyDictionary<string, string> vatGroupsByItem,
        DateTime now,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // An empty answer is not "every item lost its tax group". Taking it literally would empty
        // the table and put every till back on the standard rate for everything - the exact fault
        // this table exists to prevent, caused by the sync meant to prevent it.
        if (vatGroupsByItem.Count == 0)
        {
            logger.LogWarning(
                "SAP returned no item VAT groups. Keeping the {Count} already stored.",
                await dbContext.SapItemTaxGroups.CountAsync(cancellationToken));
            return new ApplyOutcome(0, []);
        }

        var existing = await dbContext.SapItemTaxGroups
            .ToDictionaryAsync(row => row.ItemCode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var added = 0;
        var changed = new List<ItemTaxGroupChange>();

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

                    changed.Add(new ItemTaxGroupChange(code, row.VatGroup, group));
                    row.VatGroup = group;
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

        return new ApplyOutcome(added, changed.OrderBy(change => change.ItemCode).ToList());
    }

    internal readonly record struct ApplyOutcome(int Added, IReadOnlyList<ItemTaxGroupChange> Changed);
}
