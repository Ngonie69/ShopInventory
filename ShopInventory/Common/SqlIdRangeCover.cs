using System.Globalization;

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

    /// <summary>
    /// Like <see cref="Cover"/>, but no returned range spans numbers of different decimal widths.
    /// </summary>
    /// <remarks>
    /// For columns that hold a number as text — SAP stores the source document number in
    /// <c>RIN1.BaseRef</c> as a string — a range has to be compared lexicographically, and that only
    /// agrees with numeric ordering when both ends have the same digit count. <c>'90' &lt; '100'</c>
    /// is false as text. Splitting at the powers of ten keeps each emitted range safe to compare as
    /// text; the split points are constants, so the statements still recur.
    /// </remarks>
    public static IReadOnlyList<(int Start, int End)> CoverSameDigitWidth(
        IEnumerable<int> ids,
        int bucketSize = DefaultBucketSize)
    {
        var ranges = new List<(int Start, int End)>();

        foreach (var (start, end) in Cover(ids, bucketSize))
        {
            // Ids are positive, so a bucket starting at 0 begins at 1.
            var segmentStart = Math.Max(start, 1);

            while (segmentStart <= end)
            {
                var width = segmentStart.ToString(CultureInfo.InvariantCulture).Length;
                var widthCeiling = (int)Math.Min(Math.Pow(10, width) - 1, int.MaxValue);
                var segmentEnd = Math.Min(end, widthCeiling);

                ranges.Add((segmentStart, segmentEnd));

                if (segmentEnd == int.MaxValue)
                {
                    break;
                }

                segmentStart = segmentEnd + 1;
            }
        }

        return ranges;
    }
}
