namespace ShopInventory.Features.Prices;

internal static class PriceCatalogSyncGate
{
    public const string ClusterLockName = "price-catalog-sync-run";

    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    public static async Task<Lease?> TryEnterAsync(CancellationToken cancellationToken)
    {
        if (!await SyncLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        return new Lease();
    }

    public sealed class Lease : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SyncLock.Release();
        }
    }
}
