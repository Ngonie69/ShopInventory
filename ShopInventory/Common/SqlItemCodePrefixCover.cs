namespace ShopInventory.Common;

/// <summary>
/// Turns a set of SAP item codes into a small number of fixed prefix buckets that cover them.
/// </summary>
/// <remarks>
/// The string counterpart of <see cref="SqlIdRangeCover"/>, and it exists for the same reason. A
/// SQLQueries object is keyed by its SQL text and cannot be deleted once created — DELETE against a
/// large OUQR is killed by a gateway timeout without committing — so a statement embedding the
/// caller's exact <c>IN</c> list is unique per request and leaves a permanent row behind every time
/// it runs. Item-code sets shift as stock and order lines change, so that shape never converges.
///
/// Covering the codes with fixed-width prefixes and filtering the surplus in memory keeps the
/// statement text drawn from a small recurring set — one per prefix ever touched, times whatever
/// else scopes the query — so repeat requests reuse the SAP object instead of minting another.
/// Alignment is what makes it recur: <c>CHE011</c> and <c>CHE042</c> both map to <c>CHE</c>
/// regardless of what else was requested alongside them.
///
/// Three characters is the natural width for this catalogue, whose codes are a three-letter family
/// followed by three digits (<c>CHE011</c>, <c>NRI049</c>, <c>PIC003</c>). That bounds the bucket
/// count at the number of families, which is dozens, not the number of distinct subsets, which is
/// unbounded. Nothing here depends on that shape though — a shorter code simply becomes its own
/// bucket.
/// </remarks>
public static class SqlItemCodePrefixCover
{
    /// <summary>
    /// Default bucket width. Wide enough that a page of related items shares a bucket, narrow
    /// enough that the bucket does not pull the whole catalogue back.
    /// </summary>
    public const int DefaultPrefixLength = 3;

    /// <summary>
    /// Returns the prefixes covering <paramref name="itemCodes"/>, upper-cased, ordered and
    /// de-duplicated. Null, empty and whitespace-only codes are ignored; a code shorter than
    /// <paramref name="prefixLength"/> contributes itself.
    /// </summary>
    public static IReadOnlyList<string> Cover(
        IEnumerable<string?> itemCodes,
        int prefixLength = DefaultPrefixLength)
    {
        ArgumentNullException.ThrowIfNull(itemCodes);
        ArgumentOutOfRangeException.ThrowIfLessThan(prefixLength, 1);

        var prefixes = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var itemCode in itemCodes)
        {
            if (string.IsNullOrWhiteSpace(itemCode))
            {
                continue;
            }

            var trimmed = itemCode.Trim().ToUpperInvariant();
            prefixes.Add(trimmed.Length <= prefixLength ? trimmed : trimmed[..prefixLength]);
        }

        return prefixes.ToList();
    }

    /// <summary>
    /// Whether <paramref name="itemCode"/> falls in the bucket <paramref name="prefix"/> names.
    /// </summary>
    /// <remarks>
    /// The in-memory half of the cover. The SQL side fetches a superset — every code sharing the
    /// prefix, not only the ones asked for — so callers filter with this and then against their own
    /// requested set. Ordinal and case-insensitive, matching how the codes are compared everywhere
    /// else in this client.
    /// </remarks>
    public static bool IsInBucket(string? itemCode, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        return !string.IsNullOrWhiteSpace(itemCode) &&
               itemCode.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
