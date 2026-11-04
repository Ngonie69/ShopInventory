namespace ShopInventory.Features.TradingCutoff;

/// <summary>The answer to "may this account still trade right now?"</summary>
/// <param name="IsClosed">True once the cut-off time has passed today.</param>
/// <param name="ClosedAt">The CAT time trading closed at; null when open.</param>
/// <param name="ByAccount">True when the account's own exception closed it rather than its channel.</param>
public sealed record TradingCutoffDecision(bool IsClosed, TimeOnly? ClosedAt, bool ByAccount)
{
    public static TradingCutoffDecision Open { get; } = new(false, null, false);
}

/// <summary>
/// The cut-off itself. Pure and static so the cases are unit tests rather than a clock.
/// </summary>
/// <remarks>
/// A day closes at its cut-off and stays closed until midnight CAT; the next day opens again on its own.
/// There is no opening time: nothing trades before the morning stock read anyway, and an opening time is
/// one more thing that could strand a van at 07:00.
/// </remarks>
public static class TradingCutoffRule
{
    /// <param name="channel">The channel's setting.</param>
    /// <param name="account">The account's exception, or null when it has none.</param>
    /// <param name="nowCat">Now, in CAT.</param>
    public static TradingCutoffDecision Evaluate(
        TradingCutoffChannelSetting channel,
        TradingCutoffAccountSetting? account,
        DateTime nowCat)
    {
        var now = TimeOnly.FromDateTime(nowCat);

        switch (account?.Mode)
        {
            case TradingCutoffAccountMode.Exempt:
                return TradingCutoffDecision.Open;

            case TradingCutoffAccountMode.CutOff:
                var own = account.CutoffTime ?? channel.CutoffTime;
                return now >= own ? new TradingCutoffDecision(true, own, ByAccount: true) : TradingCutoffDecision.Open;
        }

        if (!channel.Enabled)
        {
            return TradingCutoffDecision.Open;
        }

        return now >= channel.CutoffTime
            ? new TradingCutoffDecision(true, channel.CutoffTime, ByAccount: false)
            : TradingCutoffDecision.Open;
    }
}
