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
/// A reservation holds stock while it is Pending and unexpired, with one exception: a reservation
/// behind a queued invoice that end-of-day consolidation has already posted. Consolidation marks the
/// queue entry Completed and leaves the reservation alone, so it stays Pending for up to its hour
/// while SAP has already issued the units. Counting it then would take the same units off twice.
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
        var now = DateTime.UtcNow;

        return dbContext.StockReservationLines
            .Where(line => line.ItemCode == itemCode
                && line.WarehouseCode == warehouseCode
                && line.Reservation.Status == ReservationStatus.Pending
                && line.Reservation.ExpiresAt > now
                && !disregardedReservationIds.Contains(line.Reservation.ReservationId)
                && !dbContext.InvoiceQueue.Any(queued =>
                    queued.ReservationId == line.Reservation.ReservationId
                    && queued.Status == InvoiceQueueStatus.Completed))
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

        var now = DateTime.UtcNow;

        var reservedByBatch = await dbContext.StockReservationBatches
            .Where(batch => batch.ItemCode == itemCode
                && batch.WarehouseCode == warehouseCode
                && normalizedBatchNumbers.Contains(batch.BatchNumber)
                && batch.ReservationLine.Reservation.Status == ReservationStatus.Pending
                && batch.ReservationLine.Reservation.ExpiresAt > now
                && !disregardedReservationIds.Contains(batch.ReservationLine.Reservation.ReservationId)
                && !dbContext.InvoiceQueue.Any(queued =>
                    queued.ReservationId == batch.ReservationLine.Reservation.ReservationId
                    && queued.Status == InvoiceQueueStatus.Completed))
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
