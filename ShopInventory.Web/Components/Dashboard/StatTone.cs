namespace ShopInventory.Web.Components.Dashboard;

/// <summary>
/// What a <see cref="StatCard"/> is saying about its own figure.
///
/// A dashboard that draws every number identically cannot report trouble: zero
/// blocked exceptions and four hundred of them looked the same before this
/// existed. The tone drives the card's colour, but never on its own — the card
/// also carries a <c>ToneNote</c> in words and a matching glyph, so the reading
/// survives a monochrome print and a red-green confusion.
/// </summary>
public enum StatTone
{
    /// <summary>A figure with no judgement attached. The default.</summary>
    Neutral,

    /// <summary>Confirmed good — a healthy dependency, an empty failure queue.</summary>
    Ok,

    /// <summary>Wants a look today.</summary>
    Warn,

    /// <summary>Wants a look now.</summary>
    Critical
}
