namespace ShopInventory.Common;

/// <summary>
/// Turns a set of SAP document ids into a small number of fixed, aligned ranges that cover them.
/// </summary>
/// <remarks>
/// SAP SQLQueries objects are keyed by their SQL text, and the text cannot be deleted once created
/// (DELETE against a large OUQR is killed by a gateway timeout without committing). A statement that
/// embeds the caller's exact id list is therefore unique per request and leaves a permanent row
/// behind every time it runs.
///
/// Covering the ids with aligned ranges and filtering the surplus in memory keeps the statement text
/// drawn from a small, recurring set — one per bucket of id space ever touched — so repeat requests
/// reuse the same SAP object instead of minting another. Alignment is what makes it recur: ids
/// 1024 and 1600 both map to <c>1000-1999</c> regardless of what else was requested alongside them.
/// </remarks>
public static class SqlIdRangeCover
{
    /// <summary>
    /// Default bucket width. Wide enough that consecutive documents share a bucket (so the same
    /// ranges recur across requests), narrow enough that a range query stays cheap.
    /// </summary>
    public const int DefaultBucketSize = 1000;

    /// <summary>
    /// Returns the aligned ranges covering <paramref name="ids"/>, ordered and de-duplicated.
    /// Ids of zero or less are ignored, matching the callers that treat them as unset.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> Cover(
        IEnumerable<int> ids,
        int bucketSize = DefaultBucketSize)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketSize, 1);

        var buckets = new SortedSet<int>();
        foreach (var id in ids)
        {
            if (id > 0)
            {
                buckets.Add(id / bucketSize);
            }
        }

        return buckets
            .Select(bucket => (Start: bucket * bucketSize, End: (bucket * bucketSize) + bucketSize - 1))
            .ToList();
    }
}
