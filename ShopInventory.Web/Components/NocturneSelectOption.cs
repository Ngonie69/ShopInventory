namespace ShopInventory.Web.Components;

/// <summary>
/// The read-only face of <see cref="NocturneSelectOption{TValue}"/>, which is
/// what <see cref="NocturneSelect{TValue}"/> takes.
/// </summary>
/// <remarks>
/// It exists for the <c>out</c>: a page that builds
/// <c>NocturneSelectOption&lt;string&gt;</c> rows almost always binds them to a
/// <c>string?</c> field, and the closed generic class alone makes those two
/// different types — every such call site warned CS8620. Covariance closes the
/// gap once, here, instead of at each page.
/// </remarks>
public interface INocturneSelectOption<out TValue>
{
    TValue Value { get; }
    string Label { get; }
    string? Family { get; }
    string? Hint { get; }
    bool Disabled { get; }
    bool IsUnset { get; }
    bool RuleAfter { get; }
}

/// <summary>
/// One row in a <see cref="NocturneSelect{TValue}"/> menu.
/// </summary>
/// <remarks>
/// Value, label and family travel together on purpose. Where a page keeps them
/// in three parallel switches over the same statuses, the label and the colour
/// drift apart the first time a status is added.
/// </remarks>
public sealed class NocturneSelectOption<TValue> : INocturneSelectOption<TValue>
{
    public NocturneSelectOption(TValue value, string label, string? family = null)
    {
        Value = value;
        Label = label;
        Family = family;
    }

    /// <summary>The bound value this row selects.</summary>
    public TValue Value { get; init; }

    /// <summary>What the row — and the closed trigger — reads.</summary>
    public string Label { get; init; }

    /// <summary>
    /// One of the Nocturne families: <c>neutral</c>, <c>accent</c>, <c>info</c>,
    /// <c>good</c>, <c>warn</c>, <c>bad</c>. It colours the row's swatch and its
    /// tick, and the trigger's glyph while the row is the chosen one. Null draws
    /// no swatch at all, which is the right answer for a list whose items carry
    /// no meaning beyond their text — a warehouse, a currency, a page size.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>Trailing muted text: a count, a code, a currency.</summary>
    public string? Hint { get; init; }

    /// <summary>Shown, but not selectable.</summary>
    public bool Disabled { get; init; }

    /// <summary>
    /// This row means "no filter", so choosing it leaves the trigger in its
    /// resting state rather than the accent one that says a filter is set.
    /// An empty string or a null is taken to mean this already; the flag is for
    /// the pages whose no-filter sentinel is a word — <c>"all"</c> on
    /// /customers, <c>"All"</c> on the crate pages.
    /// </summary>
    public bool IsUnset { get; init; }

    /// <summary>Draws a separator under this row — used under an "All …" row.</summary>
    public bool RuleAfter { get; init; }
}

/// <summary>
/// Builders for the shapes that come up often enough that writing them out at
/// every call site is just noise.
/// </summary>
public static class NocturneSelectOption
{
    /// <summary>A list whose values are its labels.</summary>
    public static NocturneSelectOption<string>[] FromLabels(params string[] labels) =>
        labels.Select(l => new NocturneSelectOption<string>(l, l)).ToArray();

    /// <summary>
    /// An "All …" row, ruled off from the rest. The empty string is the value
    /// every filter in this app uses for "no filter".
    /// </summary>
    public static NocturneSelectOption<string> All(string label = "All") =>
        new(string.Empty, label, "neutral") { RuleAfter = true, IsUnset = true };
}
