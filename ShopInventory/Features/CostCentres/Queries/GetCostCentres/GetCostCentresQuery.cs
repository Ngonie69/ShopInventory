using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CostCentres.Queries.GetCostCentres;

public sealed record GetCostCentresQuery() : IRequest<ErrorOr<CostCentreListResponseDto>>;
