namespace ShopInventory.Models.Entities;

/// <summary>The one place two odometer readings become a distance.</summary>
/// <remarks>
/// Every odometer reading is kilometres, the SI unit, so a distance is the difference of two
/// readings and nothing converts.
/// </remarks>
public static class OdometerDistance
{
    /// <summary>
    /// Kilometres between two readings, or null when either is missing or the odometer appears to
    /// have run backwards. A backwards reading is a typo, and a negative distance would quietly
    /// subtract from a fleet total.
    /// </summary>
    public static int? Kilometres(int? start, int? close)
    {
        if (start is not { } from || close is not { } to || to < from)
        {
            return null;
        }

        return to - from;
    }
}
