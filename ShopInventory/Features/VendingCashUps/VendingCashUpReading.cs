using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VendingCashUps;

/// <summary>A cashier-day's count laid against its sales.</summary>
public sealed record VendingCashUpReadingResult(
    IReadOnlyList<VendingCashUpCurrencyDto> Currencies,
    IReadOnlyList<VendingCashUpTenderDto> Tenders,
    bool Counted,
    bool Balanced,
    bool NeedsReason,
    bool SalesChangedSinceCount);

/// <summary>
/// Lays a saved cash-up (or none) against the sales as they stand now.
/// </summary>
/// <remarks>
/// <para>
/// The variance shown is against the sales <em>now</em>, because that is the truth about the drawer; the
/// figures the count was made against are kept beside it, and <see cref="VendingCashUpReadingResult.SalesChangedSinceCount"/>
/// says when the two differ. Approval refuses while they do, so an approved count's variance is the one
/// the approver saw.
/// </para>
/// <para>
/// With no cash-up every counted figure and every variance is null. Nothing here ever invents a zero
/// count: "not cashed up" must read as that, not as a shortfall of the whole day's takings.
/// </para>
/// <para>
/// A currency or tender that has sales but no line on a saved count (the sales arrived after it) is
/// read as counted at nothing: the count was of the whole drawer, and nothing in it was in that currency.
/// </para>
/// </remarks>
public static class VendingCashUpReading
{
    /// <summary>The currencies the vending depots take, shown on a count even on a day with no sales in them.</summary>
    public static readonly string[] DepotCurrencies = ["USD", "ZIG"];

    public static VendingCashUpReadingResult Read(
        VendingCashUpEntity? cashUp,
        IReadOnlyCollection<VendingCashierDaySales> sales,
        IReadOnlyCollection<string> alwaysShow)
    {
        var cashSales = sales
            .Where(row => row.Tender == TenderTypes.Cash)
            .GroupBy(row => row.Currency)
            .ToDictionary(group => group.Key, group => (Amount: group.Sum(row => row.Amount), Sales: group.Sum(row => row.Sales)));

        var otherSales = sales
            .Where(row => row.Tender != TenderTypes.Cash)
            .GroupBy(row => (row.Currency, row.Tender))
            .ToDictionary(group => group.Key, group => (Amount: group.Sum(row => row.Amount), Sales: group.Sum(row => row.Sales)));

        var savedCurrencies = cashUp?.Currencies.ToDictionary(line => line.Currency) ?? [];
        var savedTenders = cashUp?.Tenders.ToDictionary(line => (line.Currency, line.Tender)) ?? [];
        var counted = cashUp is not null;

        var currencyCodes = alwaysShow
            .Concat(cashSales.Keys)
            .Concat(otherSales.Keys.Select(key => key.Currency))
            .Concat(savedCurrencies.Keys)
            .Distinct()
            .OrderBy(CurrencyOrder)
            .ThenBy(code => code, StringComparer.Ordinal)
            .ToList();

        var salesChanged = false;
        var variances = new List<decimal>();

        var currencies = new List<VendingCashUpCurrencyDto>();
        foreach (var code in currencyCodes)
        {
            var expected = cashSales.TryGetValue(code, out var cash) ? cash : (Amount: 0m, Sales: 0);
            savedCurrencies.TryGetValue(code, out var saved);

            if (!counted)
            {
                currencies.Add(new VendingCashUpCurrencyDto(
                    code, expected.Amount, expected.Sales, null, null, null, null, null, null, null, []));
                continue;
            }

            var openingFloat = saved?.OpeningFloat ?? 0m;
            var countedCash = saved?.CountedCash ?? 0m;
            var countedAgainst = saved?.ExpectedCash ?? 0m;
            var variance = VendingCashUpMath.CashVariance(countedCash, openingFloat, expected.Amount);

            salesChanged |= VendingCashUpMath.Round(countedAgainst) != VendingCashUpMath.Round(expected.Amount);
            variances.Add(variance);

            var denominations = cashUp!.Denominations
                .Where(line => line.Currency == code)
                .OrderByDescending(line => line.Value)
                .Select(line => new VendingCashUpDenominationDto(line.Value, line.Quantity))
                .ToList();

            currencies.Add(new VendingCashUpCurrencyDto(
                code,
                expected.Amount,
                expected.Sales,
                countedAgainst,
                openingFloat,
                countedCash,
                saved?.Banked ?? 0m,
                saved?.DepositReference,
                variance,
                VendingCashUpMath.ToBank(countedCash, openingFloat),
                denominations));
        }

        var tenderKeys = otherSales.Keys
            .Concat(savedTenders.Keys)
            .Distinct()
            .OrderBy(key => CurrencyOrder(key.Currency))
            .ThenBy(key => key.Currency, StringComparer.Ordinal)
            .ThenBy(key => key.Tender, StringComparer.Ordinal)
            .ToList();

        var tenders = new List<VendingCashUpTenderDto>();
        foreach (var key in tenderKeys)
        {
            var expected = otherSales.TryGetValue(key, out var other) ? other : (Amount: 0m, Sales: 0);
            savedTenders.TryGetValue(key, out var saved);

            if (!counted)
            {
                tenders.Add(new VendingCashUpTenderDto(key.Currency, key.Tender, expected.Amount, expected.Sales, null, null, null));
                continue;
            }

            var declared = saved?.Declared ?? 0m;
            var countedAgainst = saved?.Expected ?? 0m;
            var variance = VendingCashUpMath.TenderVariance(declared, expected.Amount);

            salesChanged |= VendingCashUpMath.Round(countedAgainst) != VendingCashUpMath.Round(expected.Amount);
            variances.Add(variance);

            tenders.Add(new VendingCashUpTenderDto(
                key.Currency, key.Tender, expected.Amount, expected.Sales, countedAgainst, declared, variance));
        }

        var balanced = counted && VendingCashUpMath.IsBalanced(variances);
        var needsReason = counted && !balanced && string.IsNullOrWhiteSpace(cashUp!.VarianceReason);

        return new VendingCashUpReadingResult(currencies, tenders, counted, balanced, needsReason, salesChanged);
    }

    /// <summary>USD first, then ZiG, then anything else alphabetically.</summary>
    public static int CurrencyOrder(string currency) => currency switch
    {
        "USD" => 0,
        "ZIG" => 1,
        _ => 2
    };
}
