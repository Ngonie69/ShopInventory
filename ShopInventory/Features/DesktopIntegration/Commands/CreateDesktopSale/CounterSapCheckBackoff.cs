using System.Collections.Concurrent;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

/// <summary>
/// Remembers, per warehouse, that SAP's stock read just ran out of time there, so the next sales do
/// not each wait for the same hang.
/// </summary>
/// <remarks>
/// Held in memory, one per API process, because it only saves waiting. A node that has not seen the
/// timeout asks SAP once and starts its own window. Restarting the process ends every window, so the
/// worst that can happen is one more wait.
/// See <see cref="Configuration.DailyStockSettings.CounterSapBackoffSeconds"/>.
/// </remarks>
public sealed class CounterSapCheckBackoff(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _skipUntil =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the warehouse's skip window ends, if it is still open. An expired window is removed.
    /// </summary>
    public DateTimeOffset? SkippingUntil(string warehouseCode)
    {
        if (!_skipUntil.TryGetValue(warehouseCode, out var until))
        {
            return null;
        }

        if (until > clock.GetUtcNow())
        {
            return until;
        }

        // Only removes the expired value. A timeout that has just opened a new window keeps it.
        _skipUntil.TryRemove(new KeyValuePair<string, DateTimeOffset>(warehouseCode, until));
        return null;
    }

    /// <summary>
    /// A check at the warehouse ran out of time. Opens a window of <paramref name="window"/> from now
    /// and returns when it ends, or null when <paramref name="window"/> is not positive.
    /// </summary>
    public DateTimeOffset? TimedOut(string warehouseCode, TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
        {
            return null;
        }

        var until = clock.GetUtcNow().Add(window);
        _skipUntil[warehouseCode] = until;
        return until;
    }

    /// <summary>
    /// SAP answered a check at the warehouse, so it is reading again. Ends any window there.
    /// </summary>
    public void Answered(string warehouseCode) => _skipUntil.TryRemove(warehouseCode, out _);
}
