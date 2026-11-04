using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

/// <summary>The cut-off for every channel, and every selling account with its exception.</summary>
public sealed record GetTradingCutoffSettingsQuery : IRequest<ErrorOr<TradingCutoffSettings>>;
