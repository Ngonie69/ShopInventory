using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Queries.GetCrateGrvs;

public sealed record GetCrateGrvsQuery(
    string? Search,
    string? Status,
    Guid UserId
) : IRequest<ErrorOr<List<CrateGrvDto>>>;