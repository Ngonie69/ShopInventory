using System.Collections.Concurrent;
using ShopInventory.Configuration;

namespace ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;

/// <summary>
/// When each failed document is next looked up at the fiscal device. A singleton.
/// </summary>
/// <remarks>
/// A "not there yet" answer records no fiscal transaction row, on purpose: a row would become the document's latest,
/// and the latest row is what the invoice retry pass decides from. So nothing in the log moves the next look-up on,
/// and this does instead. Held in memory because forgetting costs only a look-up: a restart, or the other node,
/// asks once more.
/// </remarks>
public sealed class FiscalReceiptPickupSchedule
{
    private readonly ConcurrentDictionary<(string DocumentType, int DocNum), Entry> _entries = new();

    /// <summary>Whether a document whose latest failure was at <paramref name="lastFailedAtUtc"/> is due a look-up.</summary>
    public bool IsDue(
        string documentType,
        int docNum,
        DateTime lastFailedAtUtc,
        DateTime nowUtc,
        FiscalReceiptPickupSettings settings)
    {
        // A newer failure, from a person pressing Fiscalise again or from a retry, starts the wait over.
        if (!_entries.TryGetValue((documentType, docNum), out var entry) || entry.LastFailedAtUtc != lastFailedAtUtc)
        {
            return lastFailedAtUtc + settings.LookupDelay(0) <= nowUtc;
        }

        return entry.NextLookupAtUtc <= nowUtc;
    }

    /// <summary>The device holds no receipt the document can take yet.</summary>
    public void RecordMiss(
        string documentType,
        int docNum,
        DateTime lastFailedAtUtc,
        DateTime nowUtc,
        FiscalReceiptPickupSettings settings)
        => _entries.AddOrUpdate(
            (documentType, docNum),
            _ => new Entry(lastFailedAtUtc, 1, nowUtc + settings.LookupDelay(1)),
            (_, existing) =>
            {
                var misses = existing.LastFailedAtUtc == lastFailedAtUtc ? existing.Misses + 1 : 1;
                return new Entry(lastFailedAtUtc, misses, nowUtc + settings.LookupDelay(misses));
            });

    /// <summary>The receipt was found and recorded, or the document is no longer owed one.</summary>
    public void Forget(string documentType, int docNum) => _entries.TryRemove((documentType, docNum), out _);

    /// <summary>Drops the documents whose latest failure has left the look-back window.</summary>
    public void Prune(DateTime cutoffUtc)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastFailedAtUtc < cutoffUtc)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(DateTime LastFailedAtUtc, int Misses, DateTime NextLookupAtUtc);
}
