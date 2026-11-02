using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExchangeRates.Commands.RunMonitorCheck;

/// <summary>Runs a rate-monitor pass now, as the 15-minute job does, and returns what it found.</summary>
public sealed record RunMonitorCheckCommand : IRequest<ErrorOr<List<ExchangeRateMonitorCheckResultDto>>>;
