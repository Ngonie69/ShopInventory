using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Plants snapshot rows in whatever state a test needs them, including states the production code
/// can only reach by moving them.
/// </summary>
/// <remarks>
/// <see cref="DailyStockSnapshotItemEntity.AvailableQuantity"/> has no public setter: the figure the
/// invoicing guard reads is moved through <c>Move</c> and explained by a journal row, and closing the
/// setter is what stops a new writer appearing quietly. Tests need to start a row mid-day anyway —
/// "a row already twenty units down" is a precondition, not a story about how it got there — so they
/// reach the same internal method the production writers do, through this one named place rather
/// than fourteen scattered ones.
///
/// <para>Rows seeded through <see cref="MovedTo"/> carry no journal entry, which is deliberate: it is
/// exactly the shape of a row that moved before the journal existed, and the reconciliation's
/// balance floor is there to catch it.</para>
/// </remarks>
internal static class SnapshotRowSeeding
{
    /// <summary>A row as the morning fetch leaves it: opening and working quantity the same.</summary>
    public static DailyStockSnapshotItemEntity Opened(
        this DailyStockSnapshotItemEntity row,
        decimal quantity) =>
        row.Opening(quantity);

    /// <summary>A row that opened at one figure and has since been moved to another.</summary>
    public static DailyStockSnapshotItemEntity MovedTo(
        this DailyStockSnapshotItemEntity row,
        decimal opening,
        decimal available)
    {
        row.Opening(opening);
        row.Move(available - opening);
        return row;
    }
}
