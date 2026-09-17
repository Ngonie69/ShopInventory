using System.Collections.Concurrent;

namespace ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;

/// <summary>
/// Lets one stock fetch at a time build a warehouse's snapshot.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Nothing stopped two fetches building the same snapshot. Each reuses the header
/// the other is working on, each reads SAP, and each adds a full set of rows — no unique index stands
/// in the way — so the warehouse ends up offering every unit twice. On 2026-09-17 a fetch for KEFSHOP
/// started by hand at 08:06 was still reading SAP when a second was started at 08:11. The morning job
/// and a person pressing the button while it runs are the same collision.</para>
///
/// <para><b>Refuses rather than queues.</b> A second request arriving while one is running wants the
/// same thing the first is already doing; waiting and then running it again would read SAP twice for
/// one snapshot.</para>
///
/// <para><b>One process.</b> A singleton, so it covers the job, the endpoint and the retries inside
/// this API — which is the deployment today. It is not a lock across machines.</para>
/// </remarks>
public sealed class StockFetchGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _warehouses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claims the warehouse, or returns false when a fetch for it is already running.</summary>
    public bool TryEnter(string warehouseCode) =>
        _warehouses.GetOrAdd(warehouseCode.Trim(), _ => new SemaphoreSlim(1, 1)).Wait(0);

    /// <summary>Hands back a claim <see cref="TryEnter"/> granted.</summary>
    public void Exit(string warehouseCode) =>
        _warehouses[warehouseCode.Trim()].Release();
}
