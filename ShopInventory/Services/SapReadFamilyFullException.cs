namespace ShopInventory.Services;

/// <summary>
/// A SQLQueries read that was not sent, because every slot of its read family was taken, by reads in
/// flight or by reads SAP is presumed to be still working on. See
/// <see cref="ShopInventory.Middleware.SAPReadFamilyHandler"/>.
/// </summary>
/// <remarks>
/// Reaches callers as a timeout and is not retried; see <see cref="SapReadNotSentException"/>.
/// </remarks>
public sealed class SapReadFamilyFullException(string family, int limit, string path, DateTimeOffset? firstSlotFreesAt)
    : SapReadNotSentException(
        $"Not sent to SAP: SAP already has the {limit} {family} reads this system lets it work on at once ({path}).")
{
    /// <summary>The read family that was full.</summary>
    public string Family { get; } = family;

    /// <summary>
    /// When a slot kept for a stuck read comes free, or null when the slots were taken by reads in flight,
    /// which end within their own budget.
    /// </summary>
    public DateTimeOffset? FirstSlotFreesAt { get; } = firstSlotFreesAt;
}
