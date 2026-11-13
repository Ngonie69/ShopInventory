using System.Collections.Concurrent;
using System.Text;

namespace ShopInventory.Services.CallTimings;

/// <summary>
/// What a SAP Service Layer call is called on the call timings page: its operation, and the family it is
/// grouped under.
/// </summary>
/// <remarks>
/// <para>
/// Neither ever carries the query string or a key value. A key in parentheses — <c>Invoices(123)</c>,
/// <c>BusinessPartners('…')</c> — is dropped, so a card code never reaches the page or this process's
/// memory beyond the call itself.
/// </para>
/// <para>
/// A <c>SQLQueries('&lt;code&gt;')/List</c> read keeps its query code as its operation. Those codes are
/// this API's own names (a warehouse, an item, a price list number, a hash of the SQL text), not
/// customer data. Its family is the <c>SAP:ReadFamilies</c> family the code belongs to, the same one the
/// family cap uses; a read of no limited family is grouped by its code with the trailing twelve-character
/// SQL hash taken off, so <c>STK_ITEMS_9F09D30404FF</c> and <c>STK_ITEMS_0123456789AB</c> are one family.
/// Every other call is grouped by its method and entity: <c>POST Invoices</c>, <c>PATCH Drafts</c>.
/// </para>
/// </remarks>
public static class SapCallLabels
{
    private const int MaxLabelLength = 96;
    private const int MaxInterned = 4096;
    private const int HashLength = 12;

    private static readonly string[] ServiceLayerRoots = ["/b1s/v1/", "/b1s/v2/"];
    private static readonly ConcurrentDictionary<string, string> Interned = new(StringComparer.Ordinal);

    /// <summary>The family and operation of <paramref name="request"/>.</summary>
    public static (string Family, string Operation) Of(HttpRequestMessage request, SapReadFamilyGate? gate)
    {
        if (SapReadFamilyGate.QueryCodeOf(request) is { } queryCode)
        {
            var family = gate?.FamilyOf(queryCode)?.Name ?? WithoutSqlHash(queryCode);
            return (Intern(Truncate(family)), Intern(Truncate(queryCode)));
        }

        var operation = Intern(Truncate($"{request.Method.Method.ToUpperInvariant()} {EntityPath(request.RequestUri)}"));
        return (operation, operation);
    }

    /// <summary>
    /// The query code without a trailing <c>_</c> and twelve upper-case hex characters, the fingerprint
    /// <c>BuildContentAddressedQueryCode</c> adds; otherwise the code unchanged.
    /// </summary>
    public static string WithoutSqlHash(string queryCode)
    {
        var cut = queryCode.Length - HashLength - 1;
        if (cut <= 0 || queryCode[cut] != '_')
        {
            return queryCode;
        }

        for (var i = cut + 1; i < queryCode.Length; i++)
        {
            if (!char.IsAsciiHexDigitUpper(queryCode[i]) && !char.IsAsciiDigit(queryCode[i]))
            {
                return queryCode;
            }
        }

        return queryCode[..cut];
    }

    /// <summary>
    /// The path below the Service Layer root with every parenthesised key dropped and no query string:
    /// <c>Drafts(12)/SaveDraftToDocument</c> becomes <c>Drafts/SaveDraftToDocument</c>.
    /// </summary>
    public static string EntityPath(Uri? uri)
    {
        if (uri is null)
        {
            return "?";
        }

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        var query = path.IndexOfAny(['?', '#']);
        if (query >= 0)
        {
            path = path[..query];
        }

        foreach (var root in ServiceLayerRoots)
        {
            var at = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                path = path[(at + root.Length)..];
                break;
            }
        }

        var result = new StringBuilder(path.Length);
        var depth = 0;
        var quoted = false;
        foreach (var c in path.TrimStart('/'))
        {
            if (depth > 0)
            {
                if (c == '\'')
                {
                    quoted = !quoted;
                }
                else if (!quoted && c == '(')
                {
                    depth++;
                }
                else if (!quoted && c == ')')
                {
                    depth--;
                }

                continue;
            }

            if (c == '(')
            {
                depth = 1;
                quoted = false;
                continue;
            }

            result.Append(c);
        }

        var entity = result.ToString().Trim('/');
        return entity.Length == 0 ? "(root)" : entity;
    }

    private static string Truncate(string value) =>
        value.Length <= MaxLabelLength ? value : value[..MaxLabelLength];

    /// <summary>One string per label, so a hundred thousand kept calls share a few hundred strings.</summary>
    internal static string Intern(string value)
    {
        if (Interned.TryGetValue(value, out var existing))
        {
            return existing;
        }

        return Interned.Count < MaxInterned ? Interned.GetOrAdd(value, value) : value;
    }
}
