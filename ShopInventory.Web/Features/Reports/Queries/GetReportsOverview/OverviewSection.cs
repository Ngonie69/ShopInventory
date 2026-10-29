using System.Collections.Concurrent;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>
/// Loads one part of the Reports Hub so that a part SAP cannot answer leaves a gap rather than an
/// empty page.
/// </summary>
internal static class OverviewSection
{
    /// <summary>How long one hub read may take before its part is shown as unavailable.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);

    public static async Task<T?> LoadAsync<T>(
        string name,
        Func<CancellationToken, Task<T?>> load,
        ConcurrentBag<string> unavailable,
        ILogger logger,
        CancellationToken budgetToken,
        CancellationToken callerToken)
        where T : class
    {
        try
        {
            var value = await load(budgetToken);
            if (value is null)
            {
                unavailable.Add(name);
            }

            return value;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // The page went away or asked for another period: nobody is waiting for this part.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reports hub could not load {Section}", name);
            unavailable.Add(name);
            return null;
        }
    }

    public static DateTime Day(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);
}
