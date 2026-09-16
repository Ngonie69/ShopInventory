using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// What pending reservations are holding, read straight from the reservation tables.
/// </summary>
/// <remarks>
/// <para>
/// Depends on the database and nothing else, and that is what lets
/// <see cref="BatchInventoryValidationService"/> take it through its constructor. It used to wrap
/// <see cref="IStockReservationService"/>, which itself depends on the validation service, so the
/// provider was handed over through a setter instead — once, at startup, to the one instance that
/// startup's own scope created. The service is scoped, so every request and job got an instance that
/// had never been given a provider, and netted nothing off: reservations were invisible to invoice
/// allocation, pre-post validation and every posting job for as long as that wiring stood.
/// </para>
///
/// <para>
/// What counts as a hold is <see cref="ReservationHolds"/>'s to say — Pending, unexpired, and not
/// behind a queued invoice that end-of-day consolidation has already posted. This is the SAP-side
/// reader of that rule and <see cref="StockLedger"/> is the till-side one, and the two must agree:
/// they gate the same shelf from opposite directions, so a rule applied to one and not the other has
/// them promising different stock.
/// </para>
/// </remarks>
public sealed class ReservedQuantityProvider(ApplicationDbContext dbContext) : IReservedQuantityProvider
{
    public Task<decimal> GetReservedQuantityAsync(
        string itemCode,
        string warehouseCode,
        IReadOnlyCollection<string> disregardedReservationIds,
        CancellationToken cancellationToken = default)
    {
        return ReservationHolds.Lines(dbContext, DateTime.UtcNow)
            .Where(line => line.ItemCode == itemCode
                && line.WarehouseCode == warehouseCode
                && !disregardedReservationIds.Contains(line.Reservation.ReservationId))
            .SumAsync(line => line.ReservedQuantity, cancellationToken);
    }

    public async Task<decimal> GetReservedBatchQuantityAsync(
        string itemCode,
        string warehouseCode,
        string batchNumber,
        IReadOnlyCollection<string> disregardedReservationIds,
        CancellationToken cancellationToken = default)
    {
        var reserved = await GetReservedBatchQuantitiesAsync(
            itemCode,
            warehouseCode,
            [batchNumber],
            disregardedReservationIds,
            cancellationToken);

        return reserved.GetValueOrDefault(batchNumber.Trim());
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetReservedBatchQuantitiesAsync(
        string itemCode,
        string warehouseCode,
        IEnumerable<string> batchNumbers,
        IReadOnlyCollection<string> disregardedReservationIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedBatchNumbers = batchNumbers
            .Where(batchNumber => !string.IsNullOrWhiteSpace(batchNumber))
            .Select(batchNumber => batchNumber.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedBatchNumbers.Count == 0)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var reservedByBatch = await ReservationHolds.Batches(dbContext, DateTime.UtcNow)
            .Where(batch => batch.ItemCode == itemCode
                && batch.WarehouseCode == warehouseCode
                && normalizedBatchNumbers.Contains(batch.BatchNumber)
                && !disregardedReservationIds.Contains(batch.ReservationLine.Reservation.ReservationId))
            .GroupBy(batch => batch.BatchNumber)
            .Select(group => new
            {
                BatchNumber = group.Key,
                Quantity = group.Sum(batch => batch.ReservedQuantity)
            })
            .ToListAsync(cancellationToken);

        return reservedByBatch.ToDictionary(
            entry => entry.BatchNumber,
            entry => entry.Quantity,
            StringComparer.OrdinalIgnoreCase);
    }
}

public static class StockReservationServiceCollectionExtensions
{
    /// <summary>
    /// Registers batch validation, reservations, and the provider that connects the two.
    /// </summary>
    /// <remarks>
    /// One place for all three so the wiring a test builds is the wiring the application runs. They
    /// were registered separately and joined by a setter call at startup, and nothing noticed that the
    /// join reached one instance out of every one ever created.
    /// </remarks>
    public static IServiceCollection AddStockReservations(this IServiceCollection services)
    {
        services.AddScoped<IReservedQuantityProvider, ReservedQuantityProvider>();
        services.AddScoped<IBatchInventoryValidationService, BatchInventoryValidationService>();
        services.AddScoped<IStockReservationService, StockReservationService>();
        return services;
    }
}
