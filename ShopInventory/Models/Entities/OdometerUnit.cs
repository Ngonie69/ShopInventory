namespace ShopInventory.Models.Entities;

/// <summary>
/// What a truck's odometer counts in. Every distance the system reports is in kilometres; this says
/// what has to be done to a truck's readings to get there.
/// </summary>
/// <remarks>
/// A fact about the vehicle that is held on the route, because the route is what a van's day is
/// opened against and a rep types the reading off whichever dashboard they are sitting behind. Three
/// of the ten trucks (CBD, West 1 and Bulawayo UPC) read miles. Their readings were stored as typed
/// and subtracted as though they were kilometres, so those three showed 62% of the distance they drove.
/// </remarks>
public enum OdometerUnit
{
    Kilometres = 0,
    Miles = 1
}

/// <summary>The one place two odometer readings become a distance.</summary>
public static class OdometerDistance
{
    public const decimal KilometresPerMile = 1.609344m;

    /// <summary>The unit a report row names, read back. Anything unrecognised is kilometres.</summary>
    public static OdometerUnit ParseUnit(string? value) =>
        Enum.TryParse<OdometerUnit>(value, ignoreCase: true, out var unit) ? unit : OdometerUnit.Kilometres;

    /// <summary>
    /// Kilometres between two readings, or null when either is missing or the odometer appears to
    /// have run backwards. A backwards reading is a typo, and a negative distance would quietly
    /// subtract from a fleet total.
    /// </summary>
    public static int? Kilometres(int? start, int? close, OdometerUnit unit)
    {
        if (start is not { } from || close is not { } to || to < from)
        {
            return null;
        }

        return unit == OdometerUnit.Miles
            ? (int)Math.Round((to - from) * KilometresPerMile, MidpointRounding.AwayFromZero)
            : to - from;
    }
}
