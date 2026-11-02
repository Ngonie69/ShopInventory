using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExchangeRates.Queries.GetMonitorStatus;

/// <summary>Where the SAP rate monitor stands for each watched currency.</summary>
public sealed record GetMonitorStatusQuery : IRequest<ErrorOr<List<ExchangeRateMonitorStatusDto>>>;
