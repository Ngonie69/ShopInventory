namespace ShopInventory.Features.TradingCutoff;

/// <summary>
/// Whether one channel (shops, vending or vans) stops taking transactions at a time of day, and when.
/// </summary>
/// <param name="Enabled">Off, the channel trades until midnight as it always did.</param>
/// <param name="CutoffTime">The CAT time from which the channel is closed for the rest of the day.</param>
public sealed record TradingCutoffChannelSetting(bool Enabled, TimeOnly CutoffTime)
{
    /// <summary>The time the business asked for.</summary>
    public static readonly TimeOnly DefaultCutoffTime = new(17, 0);

    /// <summary>
    /// What a channel nobody has saved yet does: trades all day. The switch is off until an admin turns it
    /// on, so deploying it changes nothing at the counter.
    /// </summary>
    public static TradingCutoffChannelSetting Default { get; } = new(false, DefaultCutoffTime);
}
