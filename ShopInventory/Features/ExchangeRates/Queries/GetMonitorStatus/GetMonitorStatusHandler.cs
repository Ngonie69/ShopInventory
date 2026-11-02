using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.ExchangeRates.Queries.GetMonitorStatus;

public sealed class GetMonitorStatusHandler(ExchangeRateMonitorStatusReader reader)
    : IRequestHandler<GetMonitorStatusQuery, ErrorOr<List<ExchangeRateMonitorStatusDto>>>
{
    public async Task<ErrorOr<List<ExchangeRateMonitorStatusDto>>> Handle(
        GetMonitorStatusQuery request,
        CancellationToken cancellationToken) =>
        await reader.ReadAsync(DateTime.UtcNow, cancellationToken);
}
