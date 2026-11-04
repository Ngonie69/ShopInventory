using ErrorOr;
using MediatR;
using ShopInventory.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

namespace ShopInventory.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;

/// <summary>
/// Saves the whole cut-off: every channel, and the complete list of account exceptions. An account left
/// out of <paramref name="Accounts"/> follows its channel again.
/// </summary>
public sealed record UpdateTradingCutoffSettingsCommand(
    Guid CallerUserId,
    string? CallerName,
    IReadOnlyList<TradingCutoffChannelInput> Channels,
    IReadOnlyList<TradingCutoffAccountInput> Accounts
) : IRequest<ErrorOr<TradingCutoffSettings>>;

/// <param name="Channel"><c>shops</c>, <c>vending</c> or <c>vans</c>.</param>
/// <param name="Enabled">Whether the channel stops at <paramref name="CutoffTime"/>.</param>
/// <param name="CutoffTime">CAT, <c>HH:mm</c>.</param>
public sealed record TradingCutoffChannelInput(string Channel, bool Enabled, string CutoffTime);

/// <param name="UserId">The account.</param>
/// <param name="Mode"><c>FollowChannel</c>, <c>Exempt</c> or <c>CutOff</c>.</param>
/// <param name="CutoffTime">For <c>CutOff</c>, the account's own time; blank uses the channel's.</param>
public sealed record TradingCutoffAccountInput(Guid UserId, string Mode, string? CutoffTime);
