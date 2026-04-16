using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CostCentres.Queries.GetCostCentreByCode;

public sealed record GetCostCentreByCodeQuery(string CenterCode) : IRequest<ErrorOr<CostCentreDto>>;
