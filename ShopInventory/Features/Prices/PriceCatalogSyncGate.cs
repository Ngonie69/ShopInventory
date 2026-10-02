namespace ShopInventory.Features.Prices;

/// <summary>
/// Lets one run of a price sync at a time through, in this process and (by its cluster lock name)
/// across the API nodes.
/// </summary>
/// <remarks>
/// The item price catalogue and the special prices have a gate each. They write different tables,
/// so the special prices, far fewer rows, are not held behind a walk of every price list that can
/// take up to half an hour.
/// </remarks>
internal sealed class PriceCatalogSyncGate
{
    public static readonly PriceCatalogSyncGate Catalog = new("price-catalog-sync-run");

    public static readonly PriceCatalogSyncGate SpecialPrices = new("special-price-sync-run");

    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private PriceCatalogSyncGate(string clusterLockName) => ClusterLockName = clusterLockName;

    public string ClusterLockName { get; }

    public async Task<Lease?> TryEnterAsync(CancellationToken cancellationToken)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        return new Lease(_syncLock);
    }

    public sealed class Lease(SemaphoreSlim syncLock) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            syncLock.Release();
        }
    }
}
