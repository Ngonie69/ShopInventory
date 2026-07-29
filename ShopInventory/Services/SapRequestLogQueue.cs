using System.Threading.Channels;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Hand-off point between the SAP HTTP pipeline and the background writer that persists the sync
/// history rows.
/// </summary>
/// <remarks>
/// Bounded and drop-oldest on purpose. This is a rolling window of the last few SAP calls for an
/// operations screen, not an audit trail: if the writer falls behind, losing the oldest pending
/// entries is strictly better than making a SAP call wait on Postgres. <see cref="TryEnqueue"/>
/// therefore never blocks and never fails.
/// </remarks>
public sealed class SapRequestLogQueue
{
    /// <summary>
    /// Room for far more than the table retains, so a burst of SAP traffic is absorbed whole, while
    /// still bounding what a stalled writer can hold in memory.
    /// </summary>
    private const int Capacity = 1_000;

    private readonly Channel<SapConnectionLog> _queue = Channel.CreateBounded<SapConnectionLog>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public void TryEnqueue(SapConnectionLog entry) => _queue.Writer.TryWrite(entry);

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _queue.Reader.WaitToReadAsync(cancellationToken);

    public bool TryDequeue(out SapConnectionLog entry) => _queue.Reader.TryRead(out entry!);
}
