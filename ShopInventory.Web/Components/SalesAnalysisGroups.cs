using System.Globalization;

namespace ShopInventory.Web.Components;

/// <summary>
/// Turning SAP's group lookups into the rows of a <see cref="SalesAnalysisPicker"/> Group dropdown.
/// </summary>
/// <remarks>
/// <para>
/// The lookups supply the names; the picker's own options decide which of those names are worth
/// offering. SAP keeps plenty of groups this report will never see a member of — groups used only
/// for purchasing, or for partner types that never appear on a sales invoice — and a dropdown row
/// that can only ever empty the list is a trap rather than a filter.
/// </para>
/// <para>
/// Group codes are compared as text throughout. SAP hands the partner's over as a string and the
/// item's as a number, and normalising both to trimmed text once here is what stops that
/// difference being rediscovered at every comparison.
/// </para>
/// </remarks>
public static class SalesAnalysisGroups
{
    /// <summary>
    /// The dropdown rows for a picker: the groups its options actually belong to, named, and
    /// ordered the way a reader scans them rather than by code.
    /// </summary>
    public static List<SalesAnalysisPicker.GroupOption> BuildOptions(
        IReadOnlyList<SalesAnalysisPicker.Option> options,
        IEnumerable<(string Code, string? Name)> groups) =>
        BuildOptions(options.Select(option => option.Group), groups);

    /// <summary>
    /// The same, taking only the groups the picker's options belong to — for a picker whose option
    /// type is its own. /reports/order-fulfillment has one: its control is drawn on that page's
    /// tokens rather than <see cref="SalesAnalysisPicker"/>'s, but which groups are worth offering
    /// is the same question with the same answer.
    /// </summary>
    public static List<SalesAnalysisPicker.GroupOption> BuildOptions(
        IEnumerable<string?> memberGroups,
        IEnumerable<(string Code, string? Name)> groups)
    {
        var present = memberGroups
            .Where(group => !string.IsNullOrEmpty(group))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return groups
            .Where(group => present.Contains(group.Code))
            .Select(group => new SalesAnalysisPicker.GroupOption(
                group.Code,
                // An unnamed group still has to be pickable: its members exist, and hiding it would
                // put them out of reach of every group narrowing.
                string.IsNullOrWhiteSpace(group.Name) ? $"Group {group.Code}" : group.Name))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A group code as the picker matches it: trimmed, and null where there is none. Null is out of
    /// every group narrowing rather than quietly inside one.
    /// </summary>
    public static string? Normalise(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>The same, for the item side, where SAP's group code arrives as a number.</summary>
    public static string? Normalise(int? code) =>
        code?.ToString(CultureInfo.InvariantCulture);
}
