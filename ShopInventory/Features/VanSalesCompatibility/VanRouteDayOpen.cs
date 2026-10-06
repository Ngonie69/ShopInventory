using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCompatibility;

/// <summary>
/// Which of a rep's days still counts as "the open day" — the one the handset offers End Day for, and
/// the one End Day closes.
///
/// Both questions are asked here because they were once answered in two places that did not agree.
/// The current-day read took any day never closed, however old; the close only looked back
/// <see cref="Lookback"/>. A rep who forgot to close a day more than that long ago was shown it on the
/// handset as "out since 07:41" — the time, with no date — offered End Day, and refused with "There
/// is no open trading day to close". And since the dashboard offers End Day whenever there is an open
/// day, they could not start today's either. Nothing on the handset could get them out.
///
/// The close's rule is the right one: a day older than this is abandoned, not still open, and this
/// evening's mileage and takings must not be written onto it. It stays unclosed in the reports, which
/// is what happened.
/// </summary>
public static class VanRouteDayOpen
{
    /// <summary>
    /// How far back a day can still be closed. Generous enough for a handset that has been out of
    /// coverage for a long weekend, short enough that a rep who forgot to close last month does not
    /// have this evening's mileage written onto it.
    /// </summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(4);

    /// <summary>
    /// The rep's open days, most recent first, as of <paramref name="todayCat"/> — the CAT calendar day
    /// the question is asked on.
    /// </summary>
    public static IQueryable<VanRouteDayEntity> For(
        IQueryable<VanRouteDayEntity> days,
        Guid userId,
        DateTime todayCat)
    {
        var earliestTradingDate = todayCat.Date - Lookback;

        return days
            .Where(d => d.UserId == userId
                        && d.ReturnedAt == null
                        && d.TradingDate >= earliestTradingDate)
            .OrderByDescending(d => d.TradingDate);
    }
}
