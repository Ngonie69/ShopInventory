using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetItemTaxRates;

public sealed class GetItemTaxRatesHandler(
    ApplicationDbContext context,
    IOptions<TaxSettings> taxSettings,
    ILogger<GetItemTaxRatesHandler> logger
) : IRequestHandler<GetItemTaxRatesQuery, ErrorOr<ItemTaxRatesResult>>
{
    public async Task<ErrorOr<ItemTaxRatesResult>> Handle(
        GetItemTaxRatesQuery query,
        CancellationToken cancellationToken)
    {
        var tax = taxSettings.Value;

        // The same table CreateDesktopSaleHandler stamps a line's tax code from, read the same way:
        // the point of this route is that the basket a cashier totals and the invoice the platform
        // raises come from one source, so a till and SAP cannot disagree about what an item is.
        var rows = await context.SapItemTaxGroups
            .AsNoTracking()
            .OrderBy(row => row.ItemCode)
            .Select(row => new { row.ItemCode, row.VatGroup, row.ResolvedAtUtc })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new ItemTaxRateDto(
                ItemCode: row.ItemCode,
                VatGroup: row.VatGroup,
                Rate: tax.RateFor(row.VatGroup)))
            .ToList();

        if (items.Count == 0)
        {
            // Answered rather than refused, and the till keeps whatever it already holds. An empty
            // table is the warm job not having run — on a fresh database, or after it has failed —
            // and a till that threw its rates away over that would go back to charging 15.5% on
            // zero-rated goods, which is the fault this route exists to end.
            logger.LogWarning(
                "No item VAT groups are stored, so every till line falls back to the standard rate "
                + "of {DefaultRate:P2}. SapItemTaxGroupWarmJob has not completed a pass.",
                tax.VatRate);
        }
        else
        {
            // The groups actually in use, not a count. A group with no rate of its own is charged the
            // standard rate silently, and this is the line that says which one and on how many items.
            var unlisted = items
                .Where(item => !tax.RatesByTaxCode.ContainsKey(item.VatGroup))
                .GroupBy(item => item.VatGroup, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key} ({group.Count()} item(s))")
                .ToList();

            if (unlisted.Count > 0)
            {
                logger.LogWarning(
                    "Serving {Count} item tax rate(s); these VAT groups are not in Tax:RatesByTaxCode "
                    + "and are charged the standard rate: {Groups}.",
                    items.Count, string.Join(", ", unlisted));
            }
        }

        return new ItemTaxRatesResult(
            DefaultRate: tax.VatRate,
            ResolvedAtUtc: rows.Count == 0 ? null : rows.Max(row => row.ResolvedAtUtc),
            Items: items);
    }
}
