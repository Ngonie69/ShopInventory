using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CostCentres.Queries.GetCostCentresByDimension;

public sealed record GetCostCentresByDimensionQuery(int Dimension) : IRequest<ErrorOr<CostCentreListResponseDto>>;
