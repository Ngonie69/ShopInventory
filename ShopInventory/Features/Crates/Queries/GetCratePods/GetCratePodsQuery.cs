using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Queries.GetCratePods;

public sealed record GetCratePodsQuery(
    string? Search,
    string? SubmissionRole,
    Guid UserId
) : IRequest<ErrorOr<List<CratePodSubmissionDto>>>;