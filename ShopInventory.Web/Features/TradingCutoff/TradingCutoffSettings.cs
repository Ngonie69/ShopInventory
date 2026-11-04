namespace ShopInventory.Web.Features.TradingCutoff;

/// <summary>
/// The daily trading cut-off as the API reports it. Mirrors the API's <c>TradingCutoffSettings</c> by hand,
/// nullability included.
/// </summary>
public sealed record TradingCutoffSettings(
    IReadOnlyList<TradingCutoffChannel> Channels,
    IReadOnlyList<TradingCutoffAccount> Accounts,
    DateTime NowCat,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>One channel: <c>shops</c>, <c>vending</c> or <c>vans</c>.</summary>
public sealed record TradingCutoffChannel(
    string Channel,
    string Label,
    bool Enabled,
    string CutoffTime,
    bool IsClosedNow);

/// <summary>
/// One sign-in account and its exception to its channel's cut-off. <c>Mode</c> is one of
/// <see cref="TradingCutoffModes"/>.
/// </summary>
public sealed record TradingCutoffAccount(
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

/// <summary>What Settings saves for one channel.</summary>
public sealed record TradingCutoffChannelInput(string Channel, bool Enabled, string CutoffTime);

/// <summary>What Settings saves for one account; anything not sent follows its channel.</summary>
public sealed record TradingCutoffAccountInput(Guid UserId, string Mode, string? CutoffTime);

/// <summary>The account exceptions, as names, so the page and the API spell them the same way.</summary>
public static class TradingCutoffModes
{
    public const string FollowChannel = "FollowChannel";
    public const string Exempt = "Exempt";
    public const string CutOff = "CutOff";
}
