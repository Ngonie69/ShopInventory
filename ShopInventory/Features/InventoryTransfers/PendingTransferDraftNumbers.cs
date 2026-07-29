using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Allocates the human-readable reference carried by a held transfer draft.
/// </summary>
/// <remarks>
/// The sequence is derived from the highest number already issued for the year rather than a
/// database sequence, because the same schema is created by EF on both PostgreSQL and SQLite.
/// Two submissions racing can therefore pick the same number; the unique index on
/// <c>DraftNumber</c> is what actually guarantees uniqueness, and the caller retries.
/// The counter is fixed-width and zero padded so ordering the text orders the numbers.
/// </remarks>
public static class PendingTransferDraftNumbers
{
    private const string Prefix = "DT";
    private const int CounterWidth = 5;

    public static string PrefixFor(DateTime dateUtc) => $"{Prefix}-{dateUtc:yyyy}-";

    public static string Format(DateTime dateUtc, int counter) =>
        PrefixFor(dateUtc) + counter.ToString(new string('0', CounterWidth));

    /// <summary>
    /// Next unused number for the year of <paramref name="dateUtc"/>, e.g. <c>DT-2026-00007</c>.
    /// </summary>
    public static async Task<string> NextAsync(
        ApplicationDbContext context,
        DateTime dateUtc,
        CancellationToken cancellationToken)
    {
        var prefix = PrefixFor(dateUtc);
        var highest = await context.PendingInventoryTransfers
            .AsNoTracking()
            .Where(item => item.DraftNumber != null && item.DraftNumber.StartsWith(prefix))
            .OrderByDescending(item => item.DraftNumber)
            .Select(item => item.DraftNumber)
            .FirstOrDefaultAsync(cancellationToken);

        return Format(dateUtc, ParseCounter(highest, prefix) + 1);
    }

    private static int ParseCounter(string? draftNumber, string prefix)
    {
        if (string.IsNullOrEmpty(draftNumber) || draftNumber.Length <= prefix.Length)
            return 0;

        return int.TryParse(draftNumber[prefix.Length..], out var counter) ? counter : 0;
    }
}
