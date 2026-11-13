using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VendingCashUps;

/// <summary>Builds the one-day view of a cash-up that the capture form and every command answer with.</summary>
public static class VendingCashUpSheets
{
    public static Task<VendingCashUpEntity?> FindAsync(
        ApplicationDbContext context,
        Guid cashierUserId,
        string warehouseCode,
        DateTime businessDate,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? context.VendingCashUps.AsTracking() : context.VendingCashUps.AsNoTracking();
        return query
            .Include(cashUp => cashUp.Currencies)
            .Include(cashUp => cashUp.Tenders)
            .Include(cashUp => cashUp.Denominations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cashUp => cashUp.CashierUserId == cashierUserId
                && cashUp.WarehouseCode == warehouseCode
                && cashUp.BusinessDate == businessDate, cancellationToken);
    }

    public static Task<VendingCashUpEntity?> FindAsync(
        ApplicationDbContext context,
        int cashUpId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? context.VendingCashUps.AsTracking() : context.VendingCashUps.AsNoTracking();
        return query
            .Include(cashUp => cashUp.Currencies)
            .Include(cashUp => cashUp.Tenders)
            .Include(cashUp => cashUp.Denominations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(cashUp => cashUp.Id == cashUpId, cancellationToken);
    }

    public static async Task<VendingCashUpSheetDto> BuildAsync(
        ApplicationDbContext context,
        Guid cashierUserId,
        string warehouseCode,
        DateTime businessDate,
        VendingCashUpEntity? cashUp,
        Guid? callerUserId,
        CancellationToken cancellationToken)
    {
        var sales = await VendingCashUpExpectations.LoadAsync(
            context, businessDate, businessDate, cashierUserId, warehouseCode, cancellationToken);
        var directory = await VendingCashUpDirectory.LoadAsync(context, [cashierUserId], cancellationToken);

        return Build(cashierUserId, warehouseCode, businessDate, cashUp, sales, directory, callerUserId);
    }

    public static VendingCashUpSheetDto Build(
        Guid cashierUserId,
        string warehouseCode,
        DateTime businessDate,
        VendingCashUpEntity? cashUp,
        IReadOnlyCollection<VendingCashierDaySales> sales,
        VendingCashUpDirectory directory,
        Guid? callerUserId)
    {
        var reading = VendingCashUpReading.Read(cashUp, sales, VendingCashUpReading.DepotCurrencies);
        var partner = cashUp?.BusinessPartnerCode ?? directory.PartnerCode(warehouseCode, cashierUserId);

        return new VendingCashUpSheetDto(
            cashUp?.Id,
            cashUp?.Revision ?? 0,
            cashUp?.Status ?? VendingCashUpReconciliationStatus.NotCashedUp,
            cashierUserId,
            directory.Username(cashierUserId, cashUp?.CashierUsername),
            directory.Name(cashierUserId, cashUp?.CashierUsername),
            partner,
            directory.PartnerName(partner),
            warehouseCode,
            businessDate,
            reading.Currencies,
            reading.Tenders,
            cashUp?.VarianceReason,
            cashUp?.Note,
            reading.Balanced,
            reading.NeedsReason,
            reading.SalesChangedSinceCount,
            cashUp is not null && callerUserId is { } caller && SavedBy(cashUp).Contains(caller),
            cashUp?.QueryNote,
            cashUp?.QueriedByName,
            Cat(cashUp?.QueriedAt),
            cashUp?.CapturedByName,
            Cat(cashUp?.CapturedAt),
            cashUp?.UpdatedByName,
            Cat(cashUp?.UpdatedAt),
            cashUp?.ApprovedByName,
            Cat(cashUp?.ApprovedAt));
    }

    /// <summary>Everyone who has saved the count, the first capturer included.</summary>
    public static HashSet<Guid> SavedBy(VendingCashUpEntity cashUp)
    {
        var ids = cashUp.SavedByUserIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => Guid.TryParse(text, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();

        if (cashUp.CapturedByUserId is { } captured)
        {
            ids.Add(captured);
        }

        if (cashUp.UpdatedByUserId is { } updated)
        {
            ids.Add(updated);
        }

        return ids;
    }

    /// <summary>The acting user and whether they hold a role, read from the account rather than the token.</summary>
    public static async Task<(User? User, bool HasRole)> ActorAsync(
        ApplicationDbContext context,
        Guid? actingUserId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        if (actingUserId is not { } id)
        {
            return (null, false);
        }

        var user = await context.Users.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return (null, false);
        }

        return (user, user.GetAllRoles().Any(role => roles.Contains(role, StringComparer.OrdinalIgnoreCase)));
    }

    public static DateTime TodayCat() => AuditService.ToCAT(DateTime.UtcNow).Date;

    public static DateTime? Cat(DateTime? utc) => utc is { } value ? AuditService.ToCAT(value) : null;

    public static string NormalizeWarehouse(string warehouseCode) => warehouseCode.Trim().ToUpperInvariant();
}
