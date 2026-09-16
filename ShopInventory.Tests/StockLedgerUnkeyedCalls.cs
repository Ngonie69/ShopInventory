using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The ledger's three write methods without a document key, for the tests that were written before
/// there was one.
/// </summary>
/// <remarks>
/// The key is a required parameter on the real interface on purpose: it makes the compiler ask every
/// caller what identifies its document, which is how the eighth production call site was found. That
/// question has one answer for these tests — they are about what the ledger does with the units, not
/// about recognising a repeat — and answering it inline twenty-eight times would bury the tests that
/// do care under an argument that never varies.
///
/// <para>Passing no key means no deduplication, which is exactly the behaviour these tests were
/// written against. Anything testing the key itself calls the real method and says so:
/// see <see cref="StockLedgerJournalTests"/>.</para>
/// </remarks>
internal static class StockLedgerUnkeyedCalls
{
    public static Task<StockLedgerOutcome> TryCommitAsync(
        this IStockLedger ledger,
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default) =>
        ledger.TryCommitAsync(lines, reference, documentKey: null, cancellationToken);

    public static Task<IReadOnlyList<StockLedgerShortfall>> TakeSettledAsync(
        this IStockLedger ledger,
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default) =>
        ledger.TakeSettledAsync(lines, reference, documentKey: null, cancellationToken);

    public static Task ReleaseAsync(
        this IStockLedger ledger,
        IReadOnlyList<StockLedgerLine> lines,
        string reference,
        CancellationToken cancellationToken = default) =>
        ledger.ReleaseAsync(lines, reference, documentKey: null, cancellationToken);
}
