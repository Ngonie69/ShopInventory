using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

public sealed class GetTradingCutoffSettingsHandler(HttpClient httpClient, ILogger<GetTradingCutoffSettingsHandler> logger)
    : IRequestHandler<GetTradingCutoffSettingsQuery, ErrorOr<TradingCutoffSettings>>
{
    public Task<ErrorOr<TradingCutoffSettings>> Handle(GetTradingCutoffSettingsQuery request, CancellationToken cancellationToken) =>
        TradingCutoffApi.SendAsync(httpClient, logger, HttpMethod.Get, null, "load the trading cut-off", cancellationToken);
}
