using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CountVariance.Queries.GetVanCountVariance;

/// <summary>
/// Reads every count dated in the range, keeps each van's latest, and values them all at the van
/// sales price list.
/// </summary>
/// <remarks>
/// A count's header does not say which warehouse it counted — only its lines do — so every count in
/// the range is read in full, three at a time (half the application's SAP connections, as the credit
/// note approval lookups use). The range, capped by the validator, is what keeps that bounded; a
/// range holding more than <see cref="MaxCounts"/> counts is reported as truncated rather than
/// silently short.
/// </remarks>
public sealed class GetVanCountVarianceHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    ILocalPriceCatalogService priceCatalog,
    IVanSalesOrderingPolicy orderingPolicy,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetVanCountVarianceHandler> logger)
    : IRequestHandler<GetVanCountVarianceQuery, ErrorOr<VanCountVarianceReportDto>>
{
    public const int MaxCounts = 150;
    private const int ReadConcurrency = 3;

    public async Task<ErrorOr<VanCountVarianceReportDto>> Handle(
        GetVanCountVarianceQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.CountVariance.SapDisabled;

        var vans = await VanWarehouses.LoadAsync(db, cancellationToken);
        if (vans.Count == 0)
            return Errors.CountVariance.NoVans;

        var from = query.FromDate.Date;
        var to = query.ToDate.Date;
        var generatedAtUtc = DateTime.UtcNow;

        List<InventoryCounting> counts;
        try
        {
            var headers = await sapClient.GetInventoryCountingsByDateAsync(from, to, MaxCounts + 1, cancellationToken);
            counts = await ReadInFullAsync(headers.Take(MaxCounts).ToList(), cancellationToken);

            if (headers.Count > MaxCounts)
            {
                logger.LogWarning(
                    "More than {MaxCounts} inventory counts are dated {From:yyyy-MM-dd} to {To:yyyy-MM-dd}; the van consolidation read the newest {MaxCounts}",
                    MaxCounts, from, to, MaxCounts);
            }

            var report = await BuildAsync(vans, counts, cancellationToken);
            report.FromDate = from;
            report.ToDate = to;
            report.Truncated = headers.Count > MaxCounts;
            report.GeneratedAtUtc = generatedAtUtc;
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading the inventory counts dated {From:yyyy-MM-dd} to {To:yyyy-MM-dd} failed", from, to);
            return Errors.CountVariance.SapReadFailed(
                $"read the inventory counts dated {from:dd MMM yyyy} to {to:dd MMM yyyy}", ex.Message);
        }
    }

    private async Task<List<InventoryCounting>> ReadInFullAsync(
        IReadOnlyList<InventoryCounting> headers,
        CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(ReadConcurrency, ReadConcurrency);

        var reads = headers.Select(async header =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                return await sapClient.GetInventoryCountingAsync(header.DocumentEntry, cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        });

        var counts = await Task.WhenAll(reads);
        return counts.Where(count => count is not null).Select(count => count!).ToList();
    }

    private async Task<VanCountVarianceReportDto> BuildAsync(
        IReadOnlyDictionary<string, string?> vans,
        IReadOnlyList<InventoryCounting> counts,
        CancellationToken cancellationToken)
    {
        var itemCodes = counts
            .SelectMany(count => count.InventoryCountingLines ?? [])
            .Where(line => line.WarehouseCode is not null && vans.ContainsKey(line.WarehouseCode.Trim()))
            .Select(line => line.ItemCode?.Trim())
            .Where(code => !string.IsNullOrEmpty(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rules = await orderingPolicy.GetRulesAsync(cancellationToken);
        var priced = await priceCatalog.GetPricesByPriceListAsync(rules.PriceListNumber, itemCodes, cancellationToken);

        var prices = (priced.Prices ?? [])
            .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode))
            .GroupBy(price => price.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Price, StringComparer.OrdinalIgnoreCase);

        var counterNames = await CounterNames.ReadAsync(sapClient, counts, logger, cancellationToken);

        var report = VanCountConsolidation.Consolidate(vans, counts, counterNames, prices);
        report.PriceListNum = priced.PriceListNum;
        report.PriceListName = priced.PriceListName;
        report.Currency = priced.Currency;
        return report;
    }
}
