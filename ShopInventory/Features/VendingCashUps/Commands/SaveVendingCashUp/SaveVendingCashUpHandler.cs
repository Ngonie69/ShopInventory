using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VendingCashUps.Commands.SaveVendingCashUp;

/// <summary>
/// Saves a count and copies the sales it was made against onto it.
/// </summary>
/// <remarks>
/// Every currency and tender the cashier sold in that day gets a line, whether or not the form sent
/// one: a ZiG sale with no ZiG counted is a ZiG shortfall, and leaving the line off would hide it.
/// </remarks>
public sealed class SaveVendingCashUpHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<SaveVendingCashUpHandler> logger
) : IRequestHandler<SaveVendingCashUpCommand, ErrorOr<VendingCashUpSheetDto>>
{
    private static readonly string[] CountingRoles = [ApplicationRoles.Admin, ApplicationRoles.Cashier];

    public async Task<ErrorOr<VendingCashUpSheetDto>> Handle(
        SaveVendingCashUpCommand command,
        CancellationToken cancellationToken)
    {
        var (actor, mayCount) = await VendingCashUpSheets.ActorAsync(db, command.ActingUserId, CountingRoles, cancellationToken);
        if (actor is null)
        {
            return Errors.VendingCashUp.SignedInUserRequired;
        }

        if (!mayCount)
        {
            return Errors.VendingCashUp.NotAllowedToCount;
        }

        var businessDate = command.BusinessDate.Date;
        if (businessDate > VendingCashUpSheets.TodayCat())
        {
            return Errors.VendingCashUp.DateInFuture;
        }

        var warehouse = VendingCashUpSheets.NormalizeWarehouse(command.WarehouseCode);

        var cashier = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == command.CashierUserId && user.Role == ApplicationRoles.CartVendor, cancellationToken);
        if (cashier is null)
        {
            return Errors.VendingCashUp.CashierNotFound;
        }

        var sales = await VendingCashUpExpectations.LoadAsync(
            db, businessDate, businessDate, cashier.Id, warehouse, cancellationToken);

        var assigned = cashier.GetWarehouseCodes().Any(code => string.Equals(code?.Trim(), warehouse, StringComparison.OrdinalIgnoreCase));
        if (!assigned && sales.Count == 0)
        {
            return Errors.VendingCashUp.WarehouseNotTheCashiers(warehouse);
        }

        var counts = new List<(string Currency, decimal Float, decimal Counted, decimal Banked, string? Reference, List<(decimal Value, int Quantity)> Notes)>();
        foreach (var line in command.Currencies)
        {
            var currency = VendingCashUpMath.NormalizeCurrency(line.Currency);
            var notes = (line.Denominations ?? [])
                .Where(note => note.Quantity > 0)
                .Select(note => (Value: VendingCashUpMath.Round(note.Value), note.Quantity))
                .ToList();

            decimal counted;
            if (notes.Count > 0)
            {
                counted = VendingCashUpMath.DenominationTotal(notes);
                if (line.CountedCash is { } typed && VendingCashUpMath.Round(typed) != counted)
                {
                    return Errors.VendingCashUp.DenominationsDisagree(currency, counted, VendingCashUpMath.Round(typed));
                }
            }
            else if (line.CountedCash is { } typed)
            {
                counted = VendingCashUpMath.Round(typed);
            }
            else
            {
                return Errors.VendingCashUp.CountRequired(currency);
            }

            var banked = VendingCashUpMath.Round(line.Banked);
            if (banked > counted)
            {
                return Errors.VendingCashUp.BankedMoreThanCounted(currency);
            }

            counts.Add((currency, VendingCashUpMath.Round(line.OpeningFloat), counted, banked,
                string.IsNullOrWhiteSpace(line.DepositReference) ? null : line.DepositReference.Trim(), notes));
        }

        var cashUp = await VendingCashUpSheets.FindAsync(db, cashier.Id, warehouse, businessDate, tracking: true, cancellationToken);
        if (cashUp is null ? command.ExpectedRevision != 0 : cashUp.Revision != command.ExpectedRevision)
        {
            return Errors.VendingCashUp.Stale;
        }

        if (cashUp?.Status == VendingCashUpStatuses.Approved)
        {
            return Errors.VendingCashUp.AlreadyApproved;
        }

        var now = DateTime.UtcNow;
        var isNew = cashUp is null;
        if (cashUp is null)
        {
            cashUp = new VendingCashUpEntity
            {
                CashierUserId = cashier.Id,
                WarehouseCode = warehouse,
                BusinessDate = businessDate,
                CapturedByUserId = actor.Id,
                CapturedByName = actor.Username,
                CapturedAt = now
            };
            db.VendingCashUps.Add(cashUp);
        }
        else
        {
            db.VendingCashUpCurrencies.RemoveRange(cashUp.Currencies);
            db.VendingCashUpTenders.RemoveRange(cashUp.Tenders);
            db.VendingCashUpDenominations.RemoveRange(cashUp.Denominations);
            cashUp.Currencies.Clear();
            cashUp.Tenders.Clear();
            cashUp.Denominations.Clear();
            cashUp.UpdatedByUserId = actor.Id;
            cashUp.UpdatedByName = actor.Username;
        }

        var savedBy = VendingCashUpSheets.SavedBy(cashUp);
        savedBy.Add(actor.Id);

        cashUp.CashierUsername = cashier.Username;
        cashUp.BusinessPartnerCode = cashier.AssignedBusinessPartnerCode?.Trim();
        cashUp.Status = VendingCashUpStatuses.Submitted;
        cashUp.VarianceReason = string.IsNullOrWhiteSpace(command.VarianceReason) ? null : command.VarianceReason.Trim();
        cashUp.Note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        cashUp.Revision += 1;
        cashUp.UpdatedAt = now;
        cashUp.SavedByUserIds = string.Join(',', savedBy.OrderBy(id => id));

        var cashExpected = sales
            .Where(row => row.Tender == Common.Sales.TenderTypes.Cash)
            .GroupBy(row => row.Currency)
            .ToDictionary(group => group.Key, group => (Amount: group.Sum(row => row.Amount), Sales: group.Sum(row => row.Sales)));

        foreach (var currency in cashExpected.Keys.Where(code => counts.All(count => count.Currency != code)))
        {
            counts.Add((currency, 0m, 0m, 0m, null, []));
        }

        foreach (var count in counts)
        {
            var expected = cashExpected.TryGetValue(count.Currency, out var cash) ? cash : (Amount: 0m, Sales: 0);
            cashUp.Currencies.Add(new VendingCashUpCurrencyEntity
            {
                Currency = count.Currency,
                OpeningFloat = count.Float,
                ExpectedCash = expected.Amount,
                ExpectedCashSales = expected.Sales,
                CountedCash = count.Counted,
                Banked = count.Banked,
                DepositReference = count.Reference
            });

            foreach (var note in count.Notes)
            {
                cashUp.Denominations.Add(new VendingCashUpDenominationEntity
                {
                    Currency = count.Currency,
                    Value = note.Value,
                    Quantity = note.Quantity
                });
            }
        }

        var declared = command.Tenders
            .Select(line => (Currency: VendingCashUpMath.NormalizeCurrency(line.Currency),
                Tender: SaveVendingCashUpValidator.TenderName(line.Tender),
                Declared: VendingCashUpMath.Round(line.Declared)))
            .ToList();
        var otherExpected = sales
            .Where(row => row.Tender != Common.Sales.TenderTypes.Cash)
            .GroupBy(row => (row.Currency, row.Tender))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Amount));

        foreach (var key in declared.Select(line => (line.Currency, line.Tender)).Union(otherExpected.Keys))
        {
            var declaredAmount = declared.FirstOrDefault(line => line.Currency == key.Currency && line.Tender == key.Tender).Declared;
            var expectedAmount = otherExpected.TryGetValue(key, out var amount) ? amount : 0m;
            if (declaredAmount == 0m && expectedAmount == 0m)
            {
                continue;
            }

            cashUp.Tenders.Add(new VendingCashUpTenderEntity
            {
                Currency = key.Currency,
                Tender = key.Tender,
                Expected = expectedAmount,
                Declared = declaredAmount
            });
        }

        var reading = VendingCashUpReading.Read(cashUp, sales, VendingCashUpReading.DepotCurrencies);
        if (isNew)
        {
            cashUp.BalancedOnFirstCount = reading.Balanced;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (isNew)
        {
            // Two people saved the first count of the same day at once; the unique index kept one.
            return Errors.VendingCashUp.Stale;
        }

        logger.LogInformation(
            "Vending cash-up {CashUpId} for {Cashier} at {Warehouse} on {Date:yyyy-MM-dd} saved by {Actor} (revision {Revision}, balanced {Balanced})",
            cashUp.Id, cashier.Username, warehouse, businessDate, actor.Username, cashUp.Revision, reading.Balanced);

        await LogAsync(cashUp, isNew, reading.Balanced);

        var directory = await VendingCashUpDirectory.LoadAsync(db, [cashier.Id], cancellationToken);
        return VendingCashUpSheets.Build(cashier.Id, warehouse, businessDate, cashUp, sales, directory, actor.Id);
    }

    private async Task LogAsync(VendingCashUpEntity cashUp, bool isNew, bool balanced)
    {
        try
        {
            await auditService.LogAsync(
                isNew ? "VendingCashUpCaptured" : "VendingCashUpRecounted",
                "VendingCashUp",
                cashUp.Id.ToString(),
                $"{(isNew ? "Counted" : "Recounted")} {cashUp.CashierUsername} at {cashUp.WarehouseCode} for {cashUp.BusinessDate:dd MMM yyyy}: " +
                string.Join("; ", cashUp.Currencies.Select(line =>
                    $"{line.Currency} counted {line.CountedCash:N2} (float {line.OpeningFloat:N2}, cash sales {line.ExpectedCash:N2}, banked {line.Banked:N2})")) +
                (balanced ? ". Balanced." : $". Variance. Reason: {cashUp.VarianceReason ?? "none given"}"),
                true);
        }
        catch
        {
        }
    }
}
