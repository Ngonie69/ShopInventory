using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Commands.BackfillProductDetails;

public sealed class BackfillProductDetailsHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<BackfillProductDetailsHandler> logger
) : IRequestHandler<BackfillProductDetailsCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        BackfillProductDetailsCommand command,
        CancellationToken cancellationToken)
    {
        // Find all merchandiser products missing any denormalized field
        var itemCodes = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.Category == null || mp.Category == ""
                || mp.BarCode == null || mp.BarCode == ""
                || mp.UoM == null || mp.UoM == "")
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (itemCodes.Count == 0)
            return 0;

        logger.LogInformation("Backfilling denormalized fields for {Count} distinct item codes", itemCodes.Count);

        // Fetch details from SAP in batches
        var allDetails = new Dictionary<string, ProductDetail>();
        const int batchSize = 200;

        for (int i = 0; i < itemCodes.Count; i += batchSize)
        {
            var batch = itemCodes.Skip(i).Take(batchSize).ToList();
            try
            {
                var inClause = string.Join(",", batch.Select(c => $"'{c.Replace("'", "''")}'"));
                var sqlText = $@"
                    SELECT T0.""ItemCode"", T0.""CodeBars"" AS ""BarCode"",
                           T0.""SalUnitMsr"" AS ""UoM"", T0.""U_ItemGroup"" AS ""Category""
                    FROM OITM T0
                    WHERE T0.""ItemCode"" IN ({inClause})";

                var rows = await sapClient.ExecuteRawSqlQueryAsync(
                    "MerchBackfill", "Merchandiser Product Backfill", sqlText, cancellationToken);

                foreach (var row in rows)
                {
                    var code = row.GetValueOrDefault("ItemCode")?.ToString();
                    if (!string.IsNullOrEmpty(code))
                    {
                        allDetails[code] = new ProductDetail(
                            (row.GetValueOrDefault("BarCode") ?? row.GetValueOrDefault("CodeBars"))?.ToString(),
                            (row.GetValueOrDefault("UoM") ?? row.GetValueOrDefault("SalUnitMsr"))?.ToString(),
                            (row.GetValueOrDefault("Category") ?? row.GetValueOrDefault("U_ItemGroup"))?.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch SAP details for batch starting at index {Index}", i);
            }
        }

        // Update all matching rows
        int updated = 0;
        var productsToUpdate = await context.MerchandiserProducts
            .Where(mp => mp.Category == null || mp.Category == ""
                || mp.BarCode == null || mp.BarCode == ""
                || mp.UoM == null || mp.UoM == "")
            .ToListAsync(cancellationToken);

        foreach (var product in productsToUpdate)
        {
            if (allDetails.TryGetValue(product.ItemCode, out var detail))
            {
                if (string.IsNullOrEmpty(product.BarCode))
                    product.BarCode = detail.BarCode;
                if (string.IsNullOrEmpty(product.UoM))
                    product.UoM = detail.UoM;
                if (string.IsNullOrEmpty(product.Category))
                    product.Category = detail.Category;
                updated++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Backfilled denormalized fields for {Count} merchandiser product rows", updated);

        return updated;
    }

    private sealed record ProductDetail(string? BarCode, string? UoM, string? Category);
}
