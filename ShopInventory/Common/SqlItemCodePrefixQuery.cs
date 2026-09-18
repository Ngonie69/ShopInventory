using ShopInventory.Services;

namespace ShopInventory.Common;

/// <summary>
/// Runs one fixed SAP statement over the prefix buckets that cover a set of item codes, and hands
/// back only the rows for the codes asked for.
/// </summary>
/// <remarks>
/// The replacement for <c>WHERE T0."ItemCode" IN ('A','B',…)</c> under a fixed SqlCode. The list
/// changes the statement text, and <c>EnsureSqlQueryAsync</c> PATCHes a stored query whose text
/// differs from what the caller is about to run — 30 to 43 seconds on this server — so every call
/// paid a write, and two callers sharing the code could overwrite each other's statement between
/// the PATCH and the read and each get the other's rows.
///
/// Here the statement never varies. It must filter on <c>T0."ItemCode" LIKE :prefix</c>, which is
/// bound per bucket as <c>ABC%</c>; the buckets come from <see cref="SqlItemCodePrefixCover"/>, so
/// one call per item family returns a bounded superset, filtered back to the requested codes here.
/// The statement should also <c>ORDER BY T0."ItemCode"</c>: SAP pages the result through
/// <c>$skip</c>, and a statement with no order is not guaranteed to page consistently.
///
/// The first bucket is awaited alone. The code may not be verified yet (first use, or the hour-long
/// verification lapsed) and two concurrent first calls would both try to create or repair it. The
/// remaining buckets then run at most <see cref="MaxConcurrentBuckets"/> at a time, leaving most of
/// the six process-wide SAP slots to everyone else.
///
/// Matching against the requested codes is ordinal — case-sensitive and exact, as the <c>IN</c>
/// list it replaces was on HANA. One gap: the bucket is bound upper-cased, and HANA's <c>LIKE</c>
/// is case-sensitive, so an item whose code does not start with three upper-case characters is not
/// fetched. SAP's catalogue codes here are all upper-case families (<c>CHE011</c>), which is the
/// assumption every other prefix-cover query in this code base already makes.
/// </remarks>
public static class SqlItemCodePrefixQuery
{
    /// <summary>The parameter name the statement binds the bucket to: <c>:prefix</c>.</summary>
    public const string PrefixParameter = "prefix";

    /// <summary>Buckets in flight at once after the first.</summary>
    public const int MaxConcurrentBuckets = 3;

    /// <summary>
    /// Runs <paramref name="sqlText"/> once per prefix bucket covering <paramref name="itemCodes"/>
    /// and returns the rows whose <paramref name="itemCodeColumn"/> is one of the requested codes.
    /// </summary>
    /// <param name="sapClient">The Service Layer client.</param>
    /// <param name="queryCode">Fixed SqlCode. One statement per code, never shared by another.</param>
    /// <param name="queryName">Display name stored with the query.</param>
    /// <param name="sqlText">Constant statement filtering on <c>LIKE :prefix</c>.</param>
    /// <param name="itemCodes">The codes wanted. Null and blank entries are ignored; none left
    /// means no SAP call and an empty result.</param>
    /// <param name="cancellationToken">Cancels the SAP calls.</param>
    /// <param name="parameters">Other bound values the statement declares, the same on every
    /// bucket. Must not contain <see cref="PrefixParameter"/>.</param>
    /// <param name="itemCodeColumn">The result column holding the item code.</param>
    /// <returns>
    /// Rows in bucket order (buckets sorted ordinally), each bucket in the order SAP returned it.
    /// A caller that needs another order sorts the result.
    /// </returns>
    public static async Task<List<Dictionary<string, object?>>> ExecuteForItemCodesAsync(
        this ISAPServiceLayerClient sapClient,
        string queryCode,
        string queryName,
        string sqlText,
        IEnumerable<string?> itemCodes,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? parameters = null,
        string itemCodeColumn = "ItemCode")
    {
        ArgumentNullException.ThrowIfNull(sapClient);
        ArgumentNullException.ThrowIfNull(itemCodes);

        if (parameters?.ContainsKey(PrefixParameter) == true)
        {
            throw new ArgumentException($"'{PrefixParameter}' is bound per bucket.", nameof(parameters));
        }

        var requested = new HashSet<string>(
            itemCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!),
            StringComparer.Ordinal);

        var prefixes = SqlItemCodePrefixCover.Cover(requested);
        if (prefixes.Count == 0)
        {
            return [];
        }

        var buckets = new List<Dictionary<string, object?>>[prefixes.Count];

        async Task RunBucketAsync(int index, CancellationToken token)
        {
            var bound = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrefixParameter] = prefixes[index] + "%"
            };

            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    bound[name] = value;
                }
            }

            buckets[index] = await sapClient.ExecuteParameterisedSqlQueryAsync(
                queryCode,
                queryName,
                sqlText,
                bound,
                token);
        }

        await RunBucketAsync(0, cancellationToken);

        if (prefixes.Count > 1)
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(1, prefixes.Count - 1),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentBuckets,
                    CancellationToken = cancellationToken
                },
                async (index, token) => await RunBucketAsync(index, token));
        }

        var rows = new List<Dictionary<string, object?>>();
        for (var index = 0; index < prefixes.Count; index++)
        {
            foreach (var row in buckets[index])
            {
                var code = row.GetValueOrDefault(itemCodeColumn)?.ToString();

                // Requested, and fetched by its own bucket: a short code's bucket (AB%) also
                // returns the rows of any longer family it prefixes (ABC…), which that family's
                // own bucket already returned.
                if (code is not null &&
                    requested.Contains(code) &&
                    string.Equals(SqlItemCodePrefixCover.PrefixOf(code), prefixes[index], StringComparison.Ordinal))
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }
}
