using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Queries.ValidateBulkCratePods;

public sealed record ValidateBulkCratePodsQuery(
    IReadOnlyList<int> InvoiceDocNums,
    string? SubmissionRole,
    Guid UserId) : IRequest<ErrorOr<BulkCratePodValidationResponseDto>>;