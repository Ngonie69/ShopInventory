namespace ShopInventory.Features.RouteCustomers;

/// <summary>The verdict on one outlet of an imported calling schedule.</summary>
public static class RouteScheduleMatchStatus
{
    /// <summary>The name is the shop's name, give or take case, spacing and punctuation.</summary>
    public const string Exact = "Exact";

    /// <summary>
    /// The same once trade words are set aside — "Zebra" and "Zebra Investments". Saved like an exact
    /// match, but shown so the office can overrule it.
    /// </summary>
    public const string Close = "Close";

    /// <summary>The office chose the shop.</summary>
    public const string Picked = "Picked";

    /// <summary>More than one shop on the truck has this name. Not saved until the office picks one.</summary>
    public const string Ambiguous = "Ambiguous";

    /// <summary>No shop on the truck has this name. Nearest spellings are offered, never assumed.</summary>
    public const string Unmatched = "Unmatched";

    /// <summary>The office chose to leave it out.</summary>
    public const string Excluded = "Excluded";

    /// <summary>The office picked a shop that is not an active shop on this truck.</summary>
    public const string PickRejected = "PickRejected";

    /// <summary>The sheet is not tied to a truck, so there is nothing to match against.</summary>
    public const string NoTruck = "NoTruck";

    public static bool IsScheduled(string status) => status is Exact or Close or Picked;
}

/// <summary>
/// Matches outlet names off the office's route workbook to the shops a truck holds.
/// </summary>
/// <remarks>
/// <para>
/// The workbook names outlets and carries no codes — its OUTLET ID columns are empty — while the shops
/// were typed in on the handsets by the reps. So the two lists only meet on names, both spelt by hand.
/// </para>
/// <para>
/// The rule is to save only what is certain and to offer the rest. Two names match when they are the
/// same letters and digits ("Kudu2", "KUDU 2"), or the same once trade words are set aside ("Zebra",
/// "Zebra Investments"). Anything looser — a misspelling, a shortened name — is offered as a candidate
/// for the office to choose, never applied. A shop scheduled on the wrong day moves the CCR for two
/// days; one left out only lowers it for one, and the review page shows it.
/// </para>
/// <para>
/// A name that more than one shop carries is ambiguous rather than matched to all of them. About one
/// shop in eight on a truck repeats a name already there; those are mostly the same shop registered
/// twice, and scheduling both would count one call as two in the CCR's denominator.
/// </para>
/// </remarks>
public sealed class RouteScheduleMatcher
{
    /// <summary>A shop on the truck.</summary>
    public sealed record Shop(int Id, string Code, string Name);

    /// <summary>What a name matched: a status from <see cref="RouteScheduleMatchStatus"/>, the shop, and anything else it might be.</summary>
    public sealed record Match(string Status, Shop? Shop, IReadOnlyList<Shop> Candidates);

    /// <summary>The most shops offered for an unmatched name.</summary>
    public const int MaxSuggestions = 3;

    /// <summary>How alike two names must be (1 is identical) to be offered as a suggestion.</summary>
    public const double SuggestionThreshold = 0.75;

    /// <summary>
    /// Words that say what kind of business it is rather than which one. Left out of the close match,
    /// so "Blessed Groceries" and "Blessed Shop" meet. Kept short on purpose: "mart" and "market" are
    /// left in, because "Food Mart" and "Food Market" are different shops in the same street.
    /// </summary>
    private static readonly HashSet<string> TradeWords = new(StringComparer.Ordinal)
    {
        "the", "shop", "shops", "tuckshop", "grocery", "groceries", "grocer", "investment", "investments",
        "trading", "tradings", "general", "dealer", "dealers", "supermarket", "store", "stores",
        "wholesale", "wholesales", "wholesaler", "wholesalers", "retail", "enterprise", "enterprises",
        "pvt", "ltd", "private", "limited",
    };

    private readonly IReadOnlyList<(Shop Shop, string Exact, string Core)> _shops;
    private readonly ILookup<string, Shop> _byExact;
    private readonly ILookup<string, Shop> _byCore;

    public RouteScheduleMatcher(IEnumerable<Shop> shops)
    {
        _shops = shops
            .Select(shop => (shop, ExactKey(shop.Name), CoreKey(shop.Name)))
            .ToList();
        _byExact = _shops.Where(s => s.Exact.Length > 0).ToLookup(s => s.Exact, s => s.Shop);
        _byCore = _shops.Where(s => s.Core.Length > 0).ToLookup(s => s.Core, s => s.Shop);
    }

    public Match Find(string name)
    {
        var exact = ExactKey(name);
        if (exact.Length == 0)
        {
            return new Match(RouteScheduleMatchStatus.Unmatched, null, []);
        }

        if (Decide(_byExact[exact].ToList(), RouteScheduleMatchStatus.Exact) is { } byExact)
        {
            return byExact;
        }

        var core = CoreKey(name);
        if (core.Length > 0 && Decide(_byCore[core].ToList(), RouteScheduleMatchStatus.Close) is { } byCore)
        {
            return byCore;
        }

        return new Match(RouteScheduleMatchStatus.Unmatched, null, Suggest(exact, core));
    }

    private static Match? Decide(List<Shop> found, string status) => found.Count switch
    {
        0 => null,
        1 => new Match(status, found[0], []),
        _ => new Match(RouteScheduleMatchStatus.Ambiguous, null, found.OrderBy(shop => shop.Code, StringComparer.Ordinal).ToList()),
    };

    private IReadOnlyList<Shop> Suggest(string exact, string core)
    {
        var mine = core.Length > 0 ? core : exact;

        return _shops
            .Select(s => (s.Shop, Score: Similarity(mine, s.Core.Length > 0 ? s.Core : s.Exact)))
            .Where(s => s.Score >= SuggestionThreshold)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Shop.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(s => s.Shop)
            .ToList();
    }

    /// <summary>The name as letters and digits only, lower case, with "&amp;" read as "and".</summary>
    public static string ExactKey(string name) =>
        new(name.Replace("&", "and", StringComparison.Ordinal)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    /// <summary><see cref="ExactKey"/> with the trade words taken out. Empty when the name is nothing but trade words.</summary>
    public static string CoreKey(string name)
    {
        var words = Words(name).Where(word => !TradeWords.Contains(word)).ToList();
        return words.Count == 0 ? string.Empty : string.Concat(words);
    }

    private static IEnumerable<string> Words(string name)
    {
        var word = new List<char>();
        foreach (var c in name.Replace("&", " and ", StringComparison.Ordinal))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Add(char.ToLowerInvariant(c));
            }
            else if (word.Count > 0)
            {
                yield return new string([.. word]);
                word.Clear();
            }
        }

        if (word.Count > 0)
        {
            yield return new string([.. word]);
        }
    }

    /// <summary>
    /// How alike two keys are, 0 to 1: one less the edit distance over the longer length, or 0.8 when
    /// one holds the other whole and the shorter is a real word's length ("riverside" in
    /// "riversidehotel").
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var edit = 1.0 - (double)Distance(a, b) / Math.Max(a.Length, b.Length);
        var contained = Math.Min(a.Length, b.Length) >= 4 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
            ? 0.8
            : 0;

        return Math.Max(edit, contained);
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
