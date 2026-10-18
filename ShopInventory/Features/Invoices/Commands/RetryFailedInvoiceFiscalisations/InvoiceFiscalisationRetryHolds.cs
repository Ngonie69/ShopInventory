using System.Collections.Concurrent;

namespace ShopInventory.Features.Invoices.Commands.RetryFailedInvoiceFiscalisations;

/// <summary>
/// The invoices the fiscalise route refused this pass, held back for a while. A singleton.
/// </summary>
/// <remarks>
/// A refusal (reposted after the SAP update, already fiscalised as a till sale, gone from SAP) records no
/// fiscal transaction row, so nothing in the log moves the invoice's next attempt on, and without this the
/// pass would read it from SAP and be refused again every two minutes. Held in memory because it costs only
/// that: a restart, or the other node, tries it once more and holds it again.
/// </remarks>
public sealed class InvoiceFiscalisationRetryHolds
{
    private readonly ConcurrentDictionary<int, DateTime> _heldUntilUtc = new();

    public void Hold(int docNum, DateTime untilUtc) => _heldUntilUtc[docNum] = untilUtc;

    public bool IsHeld(int docNum, DateTime nowUtc)
    {
        if (!_heldUntilUtc.TryGetValue(docNum, out var untilUtc))
        {
            return false;
        }

        if (untilUtc > nowUtc)
        {
            return true;
        }

        _heldUntilUtc.TryRemove(docNum, out _);
        return false;
    }
}
