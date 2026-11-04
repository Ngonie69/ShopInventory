namespace ShopInventory.Features.TradingCutoff;

/// <summary>How one sign-in account departs from its channel's cut-off.</summary>
public enum TradingCutoffAccountMode
{
    /// <summary>No exception: the account closes when its channel does. Stored as no row at all.</summary>
    FollowChannel,

    /// <summary>Never cut off, whatever the channel says.</summary>
    Exempt,

    /// <summary>
    /// Cut off at the account's own time, or the channel's time when it names none, even while the
    /// channel's switch is off.
    /// </summary>
    CutOff
}

/// <summary>One account's exception to its channel's cut-off.</summary>
/// <param name="Mode">What the account does.</param>
/// <param name="CutoffTime">Only read for <see cref="TradingCutoffAccountMode.CutOff"/>; null uses the channel's.</param>
public sealed record TradingCutoffAccountSetting(TradingCutoffAccountMode Mode, TimeOnly? CutoffTime);
