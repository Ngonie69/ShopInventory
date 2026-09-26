using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CountVariance.Queries.GetCountVariance;

/// <summary>
/// Reads the count from SAP and values its variance at the van sales price list.
/// </summary>
/// <remarks>
/// <para>
/// The price list is the one <see cref="IVanSalesOrderingPolicy"/> names — the list van customers buy
/// on — so a short on a van is valued at what it would have sold for, not at cost.
/// </para>
/// <para>
/// Prices come from the synced catalogue (<see cref="ILocalPriceCatalogService"/>), which holds them as
/// SAP's price list does: before VAT. The van catalogue adds tax on top of the same figure, so nothing
/// is taken off here.
/// </para>
/// </remarks>
public sealed class GetCountVarianceHandler(
    ISAPServiceLayerClient sapClient,
    ILocalPriceCatalogService priceCatalog,
    IVanSalesOrderingPolicy orderingPolicy,
    IOptions<SAPSettings> sapSettings,
    ILogger<GetCountVarianceHandler> logger)
    : IRequestHandler<GetCountVarianceQuery, ErrorOr<CountVarianceReportDto>>
{
    public async Task<ErrorOr<CountVarianceReportDto>> Handle(
        GetCountVarianceQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.CountVariance.SapDisabled;

        InventoryCounting? count;
        try
        {
            count = await sapClient.GetInventoryCountingAsync(query.DocumentEntry, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading SAP inventory count {DocumentEntry} failed", query.DocumentEntry);
            return Errors.CountVariance.SapReadFailed($"read inventory count {query.DocumentEntry}", ex.Message);
        }

        if (count is null)
            return Errors.CountVariance.NotFound(query.DocumentEntry);

        var generatedAtUtc = DateTime.UtcNow;
        var countLines = count.InventoryCountingLines ?? [];

        var itemCodes = countLines
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

        var (lines, totals) = CountVarianceValuation.Value(countLines, prices);
        var counterNames = await CounterNames.ReadAsync(sapClient, [count], logger, cancellationToken);

        return new CountVarianceReportDto
        {
            Document = CounterNames.Summarise(count, counterNames),
            Warehouses = lines
                .Select(line => line.WarehouseCode?.Trim())
                .Where(code => !string.IsNullOrEmpty(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            PriceListNum = priced.PriceListNum,
            PriceListName = priced.PriceListName,
            Currency = priced.Currency,
            Lines = lines,
            Totals = totals,
            GeneratedAtUtc = generatedAtUtc
        };
    }
}
