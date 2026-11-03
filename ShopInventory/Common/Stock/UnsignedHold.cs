using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// Lets go of a van sale's stock when the fiscal device refused it, and takes it back when the same sale is
/// sent again.
/// </summary>
/// <remarks>
/// <para><b>What happened before this.</b> A van sale reserves its stock and then asks the fiscal device to
/// sign. When the device refused or could not be reached, the rep was told "nothing was charged" but the
/// reservation was left holding for an hour, so a resend could find it. For that hour the van's stock read
/// low on the handset, and the van's own count refused other sales of the same goods. On 2026-11-03 the
/// fiscal platform was down from 09:00 to 09:32. SO6710 (VAN011, 24 lines) was refused at 09:25, and the
/// van showed those goods as gone until about 10:25, for a sale that never happened.</para>
///
/// <para><b>Released, not cancelled.</b> The row keeps its reference, so the resend finds it, and the
/// receipt row the attempt left is still the one the device is asked about. It is marked
/// <see cref="ReservationStatus.Expired"/>, the status every stock reader already treats as holding
/// nothing, with <see cref="ReleasedPrefix"/> on its reason so <see cref="ResumeAsync"/> knows it was let go
/// on purpose. A reservation a person cancelled is never resumed.</para>
///
/// <para><b>Taken back only after checking again.</b> The stock was free for anyone once released, so the
/// resend checks the same lines against what the van holds now, the way a first attempt is checked. If the
/// goods have gone it is refused, with nothing signed.</para>
///
/// <para><b>Only where nothing stands behind it.</b> Both directions require no SAP document, no queue
/// entry, and no receipt that was signed or that the device could not account for. Those conditions are
/// in the update's own WHERE, so a sale signed between a read and the update keeps its hold.</para>
/// </remarks>
public static class UnsignedHold
{
    /// <summary>The start of the reason on a reservation released this way.</summary>
    public const string ReleasedPrefix = "Released: the fiscal device did not sign. ";

    /// <summary>What <see cref="ResumeAsync"/> found.</summary>
    /// <param name="Resumed">The reservation holds its stock again.</param>
    /// <param name="Shortfall">
    /// The reservation was released, but the van no longer holds the goods. The reasons say which items.
    /// </param>
    public sealed record Resumption(bool Resumed, List<StockReservationErrorDto>? Shortfall)
    {
        public static readonly Resumption NotReleased = new(false, null);

        public bool IsShort => Shortfall is not null;
    }

    /// <summary>
    /// Stops <paramref name="reservationId"/> holding stock, after the device refused to sign its sale.
    /// </summary>
    /// <returns>True when the hold was released, false when something stood behind it.</returns>
    public static async Task<bool> ReleaseAsync(ApplicationDbContext db, string reservationId, string? why)
    {
        var now = DateTime.UtcNow;
        var reason = ReleasedPrefix + (string.IsNullOrWhiteSpace(why) ? "Nothing was charged." : why.Trim());

        // CancellationReason is 500 wide.
        if (reason.Length > 500)
        {
            reason = reason[..500];
        }

        var released = await Releasable(db)
            .Where(row => row.ReservationId == reservationId && row.Status == ReservationStatus.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Status, ReservationStatus.Expired)
                    .SetProperty(row => row.ExpiresAt, now)
                    .SetProperty(row => row.CancellationReason, reason),
                CancellationToken.None);

        if (released > 0)
        {
            await RefreshTrackedAsync(db, reservationId);
        }

        return released > 0;
    }

    /// <summary>
    /// Takes back the stock of a reservation <see cref="ReleaseAsync"/> let go, when the same sale is sent
    /// again under <paramref name="reference"/>.
    /// </summary>
    /// <param name="db">The request's context.</param>
    /// <param name="reservations">Checks the lines against what the van holds now.</param>
    /// <param name="reference">The van order the resend carries.</param>
    /// <param name="hold">How long the hold lasts from now.</param>
    public static async Task<Resumption> ResumeAsync(
        ApplicationDbContext db,
        IStockReservationService reservations,
        string reference,
        TimeSpan hold)
    {
        var released = await Released(db)
            .AsNoTracking()
            .Include(row => row.Lines)
            .ThenInclude(line => line.BatchAllocations)
            .FirstOrDefaultAsync(row => row.ExternalReferenceId == reference, CancellationToken.None);

        if (released is null)
        {
            return Resumption.NotReleased;
        }

        // In the units the attempt asked for, with the batches it was given, as a first attempt is checked.
        var lines = released.Lines
            .OrderBy(line => line.LineNum)
            .Select(line => new CreateStockReservationLineRequest
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.OriginalQuantity,
                UoMCode = line.UoMCode,
                WarehouseCode = line.WarehouseCode,
                UnitPrice = line.UnitPrice,
                TaxCode = line.TaxCode,
                DiscountPercent = line.DiscountPercent,
                CostCentreCode = line.CostCentreCode,
                BatchNumbers = line.BatchAllocations.Count == 0
                    ? null
                    : line.BatchAllocations
                        .Select(batch => new ReservationBatchRequest
                        {
                            BatchNumber = batch.BatchNumber,
                            Quantity = batch.ReservedQuantity
                        })
                        .ToList(),
                AutoAllocateBatches = false
            })
            .ToList();

        var (isValid, errors) = await reservations.ValidateStockAvailabilityAsync(
            lines, released.ReservationId, CancellationToken.None);

        if (!isValid)
        {
            return new Resumption(false, errors ?? []);
        }

        var now = DateTime.UtcNow;
        var expiresAt = now + hold;

        var resumed = await Released(db)
            .Where(row => row.ReservationId == released.ReservationId)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Status, ReservationStatus.Pending)
                    .SetProperty(row => row.ExpiresAt, expiresAt)
                    .SetProperty(row => row.CancellationReason, (string?)null)
                    .SetProperty(row => row.LastRenewedAt, now)
                    .SetProperty(row => row.RenewalCount, row => row.RenewalCount + 1),
                CancellationToken.None);

        if (resumed == 0)
        {
            // A concurrent resend took it back first, or a receipt appeared. Either way the caller reads the
            // reservation as it now stands.
            return Resumption.NotReleased;
        }

        await RefreshTrackedAsync(db, released.ReservationId);

        return new Resumption(true, null);
    }

    /// <summary>Reservations this class released, still with nothing standing behind them.</summary>
    private static IQueryable<StockReservationEntity> Released(ApplicationDbContext db) =>
        Releasable(db).Where(row => row.Status == ReservationStatus.Expired
            && row.CancellationReason != null
            && row.CancellationReason.StartsWith(ReleasedPrefix));

    /// <summary>
    /// Reservations no SAP document, queue entry, or signed or unaccounted-for receipt stands behind.
    /// </summary>
    private static IQueryable<StockReservationEntity> Releasable(ApplicationDbContext db) =>
        db.StockReservations.Where(row => row.SAPDocNum == null
            && !db.InvoiceQueue.Any(queued =>
                queued.ReservationId == row.ReservationId || queued.ExternalReference == row.ExternalReferenceId)
            && !db.DesktopSales.Any(sale => sale.ExternalReferenceId == row.ExternalReferenceId
                && (sale.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success
                    || sale.FiscalizationRequiresReconciliation)));

    /// <summary>
    /// ExecuteUpdate does not touch tracked copies. The poster loaded this reservation through the same
    /// context, and a stale copy would go on claiming the old status.
    /// </summary>
    private static async Task RefreshTrackedAsync(ApplicationDbContext db, string reservationId)
    {
        foreach (var entry in db.ChangeTracker.Entries<StockReservationEntity>()
                     .Where(entry => entry.Entity.ReservationId == reservationId)
                     .ToList())
        {
            await entry.ReloadAsync(CancellationToken.None);
        }
    }
}
