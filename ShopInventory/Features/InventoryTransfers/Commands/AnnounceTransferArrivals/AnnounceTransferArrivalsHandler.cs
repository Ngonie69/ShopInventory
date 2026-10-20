using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.AnnounceTransferArrivals;

/// <summary>
/// Tells the tills in a shop or vending warehouse that SAP has booked a stock transfer into it.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A till can list the transfers into its warehouse (Inventory → Transfers), but
/// nothing told the counter one had been booked. The platform's own transfer notifications go to the
/// stock controllers, or to whoever raised the request, and a transfer keyed straight into SAP passes
/// through the platform not at all. So the shop found out when it went looking.</para>
///
/// <para><b>Read from SAP, so every source is caught.</b> A transfer posted from the web, converted
/// from a till's request, posted by the transfer queue, or keyed into B1 by hand all end up as a SAP
/// <c>StockTransfers</c> document, and that is what is read. The read is
/// <see cref="ISAPServiceLayerClient.GetStockTransfersCreatedAsync"/>, the one the stock reconciliation
/// uses, over yesterday and today: SAP keeps a creation date but no time, and two days means a
/// transfer booked just before midnight is still seen by the first pass after it. Like that read, it
/// filters on the header's warehouses, so a transfer whose header names two other warehouses while a
/// line moves stock into a shop is missed.</para>
///
/// <para><b>What counts as an arrival.</b> Each document is read through
/// <see cref="UndeliveredTransfers.Movements"/>, the webhook's own reading: a line's warehouses win
/// over the header's, and a cancellation's negative line counts as stock leaving. A document is an
/// arrival for a warehouse when at least one item moves into it.</para>
///
/// <para><b>Sent to the warehouse, not to a person.</b> As <c>ReceiveNotification</c> to
/// <see cref="NotificationHub.WarehouseGroup"/>, the group a till joins from the warehouse claim on
/// its token and the shape of <see cref="NotificationDto"/>. The till already shows that method as a
/// Windows notification, so it needs no change to show these. Not stored as a
/// <see cref="Notification"/>: the audience rules have no notion of a warehouse, and a
/// broadcast on category "InventoryTransfer" reaches the stock controllers, who did not ask for one
/// alert per shop delivery. SignalR keeps no backlog, so a till that is closed when a transfer is
/// announced misses the alert; the transfer is in its Transfers tab either way.</para>
///
/// <para><b>Once per transfer and warehouse.</b> The rows in <see cref="ApplicationDbContext.TransferArrivalNotices"/>
/// are inserted before anything is sent and are unique on the pair, so a second pass, or a second
/// node, finds them or fails to insert them and sends nothing. The first pass ever records what SAP
/// already holds without sending it, along with a start marker, so deploying this does not announce
/// two days of transfers at once — and a first pass that finds nothing still leaves the marker, so
/// the next arrival is not taken for a backlog.</para>
/// </remarks>
public sealed class AnnounceTransferArrivalsHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    IHubContext<NotificationHub> hub,
    ILogger<AnnounceTransferArrivalsHandler> logger)
    : IRequestHandler<AnnounceTransferArrivalsCommand, ErrorOr<AnnounceTransferArrivalsResult>>
{
    /// <summary>The method the till's notification connection shows as a Windows notification.</summary>
    public const string MethodName = "ReceiveNotification";

    public const string Category = "InventoryTransfer";

    public async Task<ErrorOr<AnnounceTransferArrivalsResult>> Handle(
        AnnounceTransferArrivalsCommand command,
        CancellationToken cancellationToken)
    {
        var warehouses = await TillWarehousesAsync(cancellationToken);

        if (warehouses.Count == 0)
        {
            return new AnnounceTransferArrivalsResult(0, 0, 0, 0);
        }

        // SAP's CreationDate is the company's calendar date, which is CAT.
        var today = AuditService.ToCAT(command.UtcNow).Date;
        List<SapStockMovementDocument> documents;

        try
        {
            documents = await sapClient.GetStockTransfersCreatedAsync(
                warehouses, today.AddDays(-1), today, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing is recorded, so the next pass announces whatever this one could not read.
            logger.LogWarning(ex,
                "Could not read SAP's stock transfers into {WarehouseCount} till warehouses; no arrivals announced this pass",
                warehouses.Count);
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }

        var arrivals = Arrivals(documents, warehouses);
        var watching = await db.TransferArrivalNotices.AsNoTracking().AnyAsync(cancellationToken);
        var fresh = await NotYetRecordedAsync(arrivals, cancellationToken);

        if (!watching)
        {
            db.TransferArrivalNotices.Add(new TransferArrivalNoticeEntity
            {
                TransferDocEntry = TransferArrivalNoticeEntity.StartMarkerDocEntry,
                WarehouseCode = string.Empty,
                RecordedAt = command.UtcNow
            });
        }

        if (fresh.Count == 0 && watching)
        {
            return new AnnounceTransferArrivalsResult(warehouses.Count, arrivals.Count, 0, 0);
        }

        var announcedAt = watching ? command.UtcNow : (DateTime?)null;

        db.TransferArrivalNotices.AddRange(fresh.Select(arrival => new TransferArrivalNoticeEntity
        {
            TransferDocEntry = arrival.DocEntry,
            TransferDocNum = arrival.DocNum,
            WarehouseCode = arrival.WarehouseCode,
            SourceWarehouse = arrival.SourceWarehouse,
            ItemCount = arrival.ItemCount,
            RecordedAt = command.UtcNow,
            AnnouncedAt = announcedAt
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Another pass recorded one of these between the read and the save, so it owns the sending.
            db.ChangeTracker.Clear();
            logger.LogInformation(ex,
                "Transfer arrivals were recorded by another pass first; this pass sends nothing");
            return new AnnounceTransferArrivalsResult(warehouses.Count, arrivals.Count, 0, 0);
        }

        if (!watching)
        {
            logger.LogInformation(
                "Started watching {WarehouseCount} till warehouses for transfers; {Count} already in SAP were recorded without being announced",
                warehouses.Count, fresh.Count);
            return new AnnounceTransferArrivalsResult(warehouses.Count, arrivals.Count, 0, fresh.Count);
        }

        foreach (var arrival in fresh)
        {
            await SendAsync(arrival, command.UtcNow, cancellationToken);
        }

        return new AnnounceTransferArrivalsResult(warehouses.Count, arrivals.Count, fresh.Count, 0);
    }

    /// <summary>
    /// The warehouses a till sells from: each active shop's, and each active vending account's.
    /// </summary>
    /// <remarks>
    /// The same codes <c>UserInfoMapper.ResolveWarehouseCodesAsync</c> puts on those accounts' tokens,
    /// which is what decides the hub group their till joins. A van's warehouse is not here: a van
    /// handset is told about its loads by the request it raised.
    /// </remarks>
    private async Task<List<string>> TillWarehousesAsync(CancellationToken cancellationToken)
    {
        var shopWarehouses = await db.Shops
            .AsNoTracking()
            .Where(shop => shop.IsActive)
            .Select(shop => shop.WarehouseCode)
            .ToListAsync(cancellationToken);

        var vendorAssignments = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive
                           && user.Role == ApplicationRoles.CartVendor
                           && user.AssignedWarehouseCodes != null)
            .Select(user => user.AssignedWarehouseCodes!)
            .ToListAsync(cancellationToken);

        return shopWarehouses
            .Concat(vendorAssignments.SelectMany(WarehouseCodes))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>As <see cref="User.GetWarehouseCodes"/> reads the column.</summary>
    private static IEnumerable<string> WarehouseCodes(string assignedWarehouseCodes)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(assignedWarehouseCodes) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Each document that moves at least one item into one of <paramref name="warehouses"/>, per warehouse.</summary>
    internal static List<TransferArrival> Arrivals(
        IEnumerable<SapStockMovementDocument> documents,
        IReadOnlyCollection<string> warehouses)
    {
        var arrivals = new List<TransferArrival>();

        foreach (var document in documents.DistinctBy(document => document.DocEntry))
        {
            foreach (var warehouse in warehouses)
            {
                var inbound = UndeliveredTransfers.Movements([document], warehouse)
                    .Where(movement => movement.Direction == UndeliveredTransfers.In)
                    .ToList();

                if (inbound.Count == 0)
                {
                    continue;
                }

                var sources = inbound
                    .Select(movement => movement.SourceWarehouse?.Trim())
                    .Where(source => !string.IsNullOrEmpty(source))
                    .Select(source => source!.ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                arrivals.Add(new TransferArrival(
                    document.DocEntry,
                    document.DocNum,
                    Normalize(warehouse),
                    sources.Count == 0 ? null : string.Join(", ", sources),
                    inbound.Select(movement => movement.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
            }
        }

        return arrivals
            .OrderBy(arrival => arrival.DocEntry)
            .ThenBy(arrival => arrival.WarehouseCode, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<TransferArrival>> NotYetRecordedAsync(
        List<TransferArrival> arrivals,
        CancellationToken cancellationToken)
    {
        if (arrivals.Count == 0)
        {
            return [];
        }

        var docEntries = arrivals.Select(arrival => arrival.DocEntry).Distinct().ToList();

        var recorded = (await db.TransferArrivalNotices
                .AsNoTracking()
                .Where(notice => docEntries.Contains(notice.TransferDocEntry))
                .Select(notice => new { notice.TransferDocEntry, notice.WarehouseCode })
                .ToListAsync(cancellationToken))
            .Select(notice => (notice.TransferDocEntry, notice.WarehouseCode))
            .ToHashSet();

        return arrivals
            .Where(arrival => !recorded.Contains((arrival.DocEntry, arrival.WarehouseCode)))
            .ToList();
    }

    private async Task SendAsync(TransferArrival arrival, DateTime utcNow, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients
                .Group(NotificationHub.WarehouseGroup(arrival.WarehouseCode))
                .SendAsync(MethodName, Notice(arrival, utcNow), cancellationToken);

            logger.LogInformation(
                "Told the tills at {WarehouseCode} about transfer #{DocNum} (DocEntry {DocEntry}) from {SourceWarehouse}",
                arrival.WarehouseCode, arrival.DocNum, arrival.DocEntry, arrival.SourceWarehouse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded already, so it is not sent again: one missed alert beats a till told twice an hour.
            logger.LogWarning(ex,
                "Could not tell the tills at {WarehouseCode} about transfer #{DocNum}", arrival.WarehouseCode, arrival.DocNum);
        }
    }

    /// <summary>What the till shows: its title and message as they are.</summary>
    internal static NotificationDto Notice(TransferArrival arrival, DateTime utcNow)
    {
        var items = arrival.ItemCount == 1 ? "1 item" : $"{arrival.ItemCount} items";

        return new NotificationDto
        {
            Title = arrival.SourceWarehouse is { } source
                ? $"Stock transfer #{arrival.DocNum} from {source}"
                : $"Stock transfer #{arrival.DocNum}",
            Message = $"{items} transferred into {arrival.WarehouseCode}. " +
                      "Open Inventory → Transfers to check and receive it.",
            Type = "Info",
            Category = Category,
            EntityType = "InventoryTransfer",
            EntityId = arrival.DocEntry.ToString(),
            CreatedAt = utcNow,
            Data = new Dictionary<string, string>
            {
                ["transferDocEntry"] = arrival.DocEntry.ToString(),
                ["transferDocNum"] = arrival.DocNum.ToString(),
                ["toWarehouse"] = arrival.WarehouseCode,
                ["fromWarehouse"] = arrival.SourceWarehouse ?? string.Empty,
                ["itemCount"] = arrival.ItemCount.ToString()
            }
        };
    }

    private static string Normalize(string warehouseCode) => warehouseCode.Trim().ToUpperInvariant();
}
