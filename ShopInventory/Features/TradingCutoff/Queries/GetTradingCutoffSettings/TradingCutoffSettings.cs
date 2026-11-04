namespace ShopInventory.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

/// <summary>The cut-off as Settings shows it.</summary>
/// <param name="Channels">Shops, vending and vans, in that order.</param>
/// <param name="Accounts">
/// Every account that sells through one of the channels, and any other account that has an exception.
/// </param>
/// <param name="NowCat">The server's clock in CAT, so the page can say what is closed right now.</param>
/// <param name="UpdatedAtUtc">When the cut-off was last saved; null when it never was.</param>
/// <param name="UpdatedBy">Who last saved it.</param>
public sealed record TradingCutoffSettings(
    IReadOnlyList<TradingCutoffChannelView> Channels,
    IReadOnlyList<TradingCutoffAccountView> Accounts,
    DateTime NowCat,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

/// <param name="Channel"><c>shops</c>, <c>vending</c> or <c>vans</c>.</param>
/// <param name="Label">The channel's name as the sales reports use it.</param>
/// <param name="Enabled">Whether the channel stops trading at <paramref name="CutoffTime"/>.</param>
/// <param name="CutoffTime">CAT, <c>HH:mm</c>.</param>
/// <param name="IsClosedNow">Whether the channel's own setting has it closed at <c>NowCat</c>.</param>
public sealed record TradingCutoffChannelView(
    string Channel,
    string Label,
    bool Enabled,
    string CutoffTime,
    bool IsClosedNow);

/// <param name="UserId">The account.</param>
/// <param name="Username">Its sign-in name.</param>
/// <param name="DisplayName">Its person's name, when the account has one.</param>
/// <param name="Role">Its main role.</param>
/// <param name="Channel">The channel it sells in, from its roles; null for an account that does not sell.</param>
/// <param name="Detail">The shop, or the business partner, it sells as.</param>
/// <param name="IsActive">Whether it can sign in.</param>
/// <param name="Mode"><see cref="TradingCutoffAccountMode"/>'s name.</param>
/// <param name="CutoffTime">The account's own time, <c>HH:mm</c>, for <c>CutOff</c>; null uses the channel's.</param>
/// <param name="IsClosedNow">Whether this account is closed at <c>NowCat</c>, exception and channel together.</param>
public sealed record TradingCutoffAccountView(
    Guid UserId,
    string Username,
    string? DisplayName,
    string Role,
    string? Channel,
    string? Detail,
    bool IsActive,
    string Mode,
    string? CutoffTime,
    bool IsClosedNow);
