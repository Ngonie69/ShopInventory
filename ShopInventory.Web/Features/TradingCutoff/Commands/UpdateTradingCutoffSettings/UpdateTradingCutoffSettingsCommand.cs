using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;

/// <summary>
/// Saves every channel's cut-off and the complete list of account exceptions. An account left out follows
/// its channel again.
/// </summary>
public sealed record UpdateTradingCutoffSettingsCommand(
    IReadOnlyList<TradingCutoffChannelInput> Channels,
    IReadOnlyList<TradingCutoffAccountInput> Accounts) : IRequest<ErrorOr<TradingCutoffSettings>>;
