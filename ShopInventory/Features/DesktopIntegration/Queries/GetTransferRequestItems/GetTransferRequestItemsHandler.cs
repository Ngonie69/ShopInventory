using ErrorOr;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestItems;

public sealed class GetTransferRequestItemsHandler(
    ISAPServiceLayerClient sapClient,
    IMemoryCache cache,
    ILogger<GetTransferRequestItemsHandler> logger
) : IRequestHandler<GetTransferRequestItemsQuery, ErrorOr<TransferRequestItemsResult>>
{
    /// <summary>The statement as given for this list, unchanged.</summary>
    internal const string SqlText =
        "SELECT T0.\"ItemCode\", T0.\"ItemName\", T0.\"U_SalesItem\" FROM OITM T0 WHERE T0.\"U_SalesItem\" ='Yes' ORDER BY T0.\"ItemCode\"";

    private const string QueryCode = "TillTransferRequestItems";
    internal const string FreshKey = "desktop.transfer-request-items";
    private const string LastGoodKey = "desktop.transfer-request-items.last-good";

    /// <summary>
    /// How long the list is held before SAP is asked again.
    /// </summary>
    /// <remarks>
    /// The flag is set by hand in the item master and changes rarely, while every till opening its
    /// request screen reads this. SAP's request pool is shared with every interactive user, so an
    /// hour's reuse costs an item newly flagged at most an hour and saves a round trip per screen.
    /// </remarks>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    public async Task<ErrorOr<TransferRequestItemsResult>> Handle(
        GetTransferRequestItemsQuery query,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(FreshKey, out TransferRequestItemsResult? fresh) && fresh is not null)
        {
            return fresh;
        }

        try
        {
            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                QueryCode,
                "Till Transfer Request Items",
                SqlText,
                cancellationToken);

            var items = rows
                .Select(row => new
                {
                    Code = row.GetValueOrDefault("ItemCode")?.ToString()?.Trim(),
                    Name = row.GetValueOrDefault("ItemName")?.ToString()?.Trim(),
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Code))
                .GroupBy(row => row.Code!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new TransferRequestItemDto(
                    group.Key,
                    group.Select(row => row.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key))
                .OrderBy(item => item.ItemCode, StringComparer.Ordinal)
                .ToList();

            var result = new TransferRequestItemsResult(items);

            cache.Set(FreshKey, result, CacheLifetime);
            cache.Set(LastGoodKey, result);

            logger.LogInformation("Read {Count} transfer-request items from SAP", items.Count);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A list read an hour ago is still the right list: the flag it reflects is set by hand.
            // Serving it keeps a till able to raise a request through a SAP outage, which is when a
            // shop running short is least able to wait.
            if (cache.TryGetValue(LastGoodKey, out TransferRequestItemsResult? lastGood) && lastGood is not null)
            {
                logger.LogWarning(
                    ex,
                    "Could not read transfer-request items from SAP; serving the last {Count} read",
                    lastGood.Items.Count);
                return lastGood;
            }

            logger.LogError(ex, "Could not read transfer-request items from SAP");
            return Errors.DesktopIntegration.SapError(
                "The list of requestable items could not be read from SAP.");
        }
    }
}
