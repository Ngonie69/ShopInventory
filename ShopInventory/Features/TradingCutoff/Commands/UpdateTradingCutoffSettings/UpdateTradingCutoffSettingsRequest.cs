namespace ShopInventory.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;

/// <summary>The body of <c>PUT api/trading-cutoff</c>.</summary>
public sealed record UpdateTradingCutoffSettingsRequest(
    IReadOnlyList<TradingCutoffChannelInput>? Channels,
    IReadOnlyList<TradingCutoffAccountInput>? Accounts);
