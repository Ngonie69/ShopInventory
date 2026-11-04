using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;

public sealed class UpdateTradingCutoffSettingsHandler(HttpClient httpClient, ILogger<UpdateTradingCutoffSettingsHandler> logger)
    : IRequestHandler<UpdateTradingCutoffSettingsCommand, ErrorOr<TradingCutoffSettings>>
{
    public Task<ErrorOr<TradingCutoffSettings>> Handle(UpdateTradingCutoffSettingsCommand request, CancellationToken cancellationToken) =>
        TradingCutoffApi.SendAsync(
            httpClient, logger, HttpMethod.Put,
            new
            {
                request.Channels,
                // Following the channel is the absence of an exception; only the exceptions are sent.
                Accounts = request.Accounts.Where(account => account.Mode != TradingCutoffModes.FollowChannel).ToList()
            },
            "save the trading cut-off", cancellationToken);
}
