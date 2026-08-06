namespace ShopInventory.Web.Common;

/// <summary>
/// Works out which of an account's warehouses a dashboard should open on.
/// </summary>
/// <remarks>
/// The depot and stock dashboards both offer a warehouse picker over the codes on
/// the account, and both used to open on whichever code the claims happened to
/// list first — arbitrary, and usually the general warehouse rather than the site
/// the controller works out of.
/// <para>
/// The operational sites are the ones SAP describes as a centre ("Kefalos
/// Bulawayo Centre", "Cortina Machipisa Centre"), so a centre is preferred. That
/// alone does not identify one warehouse — several accounts hold more than one —
/// so the account's assigned section breaks the tie by naming its own place.
/// </para>
/// </remarks>
public static class HomeDepotResolver
{
    /// <summary>
    /// Words that say what kind of place a warehouse is rather than which one, so
    /// they must never make a section and a warehouse look like the same site.
    /// <para>
    /// Two things need no entry here. Company names ("Kefalos", "Cortina"),
    /// because no section carries one and they can never match in the first
    /// place; and abbreviations like "DC", which the length rule in
    /// <see cref="PlaceWords"/> drops before this list is consulted.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "centre", "center", "warehouse", "depot", "store", "stores", "main", "general", "and", "the"
    };

    private static readonly char[] WordSeparators = [' ', '-', '–', ',', '.', '/', '\\', '(', ')'];

    /// <summary>
    /// Resolution order: the centre naming the account's own section, then any
    /// centre, then a warehouse of any kind naming that section, then the first
    /// code on the account.
    /// </summary>
    /// <param name="codes">The warehouse codes the picker is offering, in the order it offers them.</param>
    /// <param name="warehouseNames">Warehouse code to the name master data holds — SAP's warehouse description.</param>
    /// <param name="assignedSection">
    /// The account's assigned section, in any of the spellings
    /// <see cref="AssignedSectionOptions"/> accepts. Null or unknown simply leaves
    /// the tie to be broken by the order the codes arrived in.
    /// </param>
    /// <returns>A code from <paramref name="codes"/>, or null when it is empty.</returns>
    public static string? Resolve(
        IReadOnlyCollection<string> codes,
        IReadOnlyDictionary<string, string> warehouseNames,
        string? assignedSection)
    {
        if (codes.Count == 0) return null;

        string NameOf(string code) =>
            warehouseNames.TryGetValue(code, out var name) ? name : string.Empty;

        var sectionWords = PlaceWords(AssignedSectionOptions.Normalize(assignedSection)).ToList();

        var centres = codes.Where(code => IsCentre(NameOf(code))).ToList();

        return BestBySection(centres, NameOf, sectionWords)
            ?? centres.FirstOrDefault()
            ?? BestBySection(codes, NameOf, sectionWords)
            ?? codes.First();
    }

    /// <summary>
    /// Whether a warehouse name reads as one of the operational centres rather
    /// than a general or holding warehouse. Both spellings are accepted because
    /// SAP carries whichever the site was named in.
    /// </summary>
    public static bool IsCentre(string? warehouseName) =>
        !string.IsNullOrWhiteSpace(warehouseName)
        && (warehouseName.Contains("centre", StringComparison.OrdinalIgnoreCase)
            || warehouseName.Contains("center", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The candidate whose name shares the most place words with the section —
    /// so "Cheeseman DC Vic Falls" prefers the Vic Falls warehouse over the other
    /// Cheeseman ones. The sort is stable, so candidates that match equally well
    /// keep the order they were offered in, and one that shares no word at all is
    /// never returned.
    /// </summary>
    private static string? BestBySection(
        IEnumerable<string> candidates,
        Func<string, string> nameOf,
        IReadOnlyCollection<string> sectionWords)
    {
        if (sectionWords.Count == 0) return null;

        return candidates
            .Select(code => new
            {
                code,
                shared = PlaceWords(nameOf(code)).Count(sectionWords.Contains)
            })
            .Where(candidate => candidate.shared > 0)
            .OrderByDescending(candidate => candidate.shared)
            .Select(candidate => candidate.code)
            .FirstOrDefault();
    }

    /// <summary>
    /// The words in a name that say which place it is. Short fragments are
    /// dropped along with the generic ones, so a two-letter abbreviation cannot
    /// tie two unrelated sites together.
    /// </summary>
    private static IEnumerable<string> PlaceWords(string? value) =>
        (value ?? string.Empty)
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.ToLowerInvariant())
            .Where(word => word.Length >= 3 && !GenericWords.Contains(word));
}
