using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.ExchangeRates.Commands.RunMonitorCheck;

public sealed class RunMonitorCheckHandler(ExchangeRateMonitor monitor)
    : IRequestHandler<RunMonitorCheckCommand, ErrorOr<List<ExchangeRateMonitorCheckResultDto>>>
{
    public async Task<ErrorOr<List<ExchangeRateMonitorCheckResultDto>>> Handle(
        RunMonitorCheckCommand request,
        CancellationToken cancellationToken)
    {
        // A failed read of SAP is a result, not an error: it is recorded on the monitor's state and
        // the page shows it.
        var results = await monitor.CheckAsync(DateTime.UtcNow, cancellationToken);

        return results.Select(r => new ExchangeRateMonitorCheckResultDto
        {
            Currency = r.Currency,
            Succeeded = r.Succeeded,
            Error = r.Error,
            AlertsRaised = r.AlertsRaised
        }).ToList();
    }
}
