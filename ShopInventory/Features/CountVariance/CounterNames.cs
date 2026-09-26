using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CountVariance;

/// <summary>
/// Who counted: SAP stores only an internal key, and nobody recognises a key.
/// </summary>
internal static class CounterNames
{
    private const string UserCounter = "ctUser";

    /// <summary>
    /// Names for every SAP user counting any of <paramref name="counts"/>, in one read. A failure costs
    /// only the names, never the list: a count is still worth showing without its counter.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, string>> ReadAsync(
        ISAPServiceLayerClient sapClient,
        IEnumerable<InventoryCounting> counts,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var keys = counts
            .Where(count => IsUserCounter(count) && count.SingleCounterID is > 0)
            .Select(count => count.SingleCounterID!.Value)
            .Distinct()
            .ToList();

        if (keys.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        try
        {
            var users = await sapClient.GetSapUsersAsync(keys, cancellationToken);
            return users
                .Select(user => (user.InternalKey, Name: string.IsNullOrWhiteSpace(user.UserName) ? user.UserCode : user.UserName))
                .Where(user => !string.IsNullOrWhiteSpace(user.Name))
                .GroupBy(user => user.InternalKey)
                .ToDictionary(group => group.Key, group => group.First().Name!.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the names of SAP users {Keys} counting stock", keys);
            return new Dictionary<int, string>();
        }
    }

    public static CountingDocumentSummaryDto Summarise(
        InventoryCounting count,
        IReadOnlyDictionary<int, string> counterNames)
        => new()
        {
            DocumentEntry = count.DocumentEntry,
            DocumentNumber = count.DocumentNumber,
            CountDate = CountVarianceValuation.ParseCountDate(count.CountDate),
            CountTime = CountVarianceValuation.FormatCountTime(count.CountTime),
            Status = CountVarianceValuation.StatusLabel(count.DocumentStatus),
            Remarks = string.IsNullOrWhiteSpace(count.Remarks) ? null : count.Remarks.Trim(),
            CounterName = IsUserCounter(count)
                          && count.SingleCounterID is { } key
                          && counterNames.TryGetValue(key, out var name)
                ? name
                : null
        };

    private static bool IsUserCounter(InventoryCounting count)
        => string.Equals(count.SingleCounterType, UserCounter, StringComparison.OrdinalIgnoreCase);
}
