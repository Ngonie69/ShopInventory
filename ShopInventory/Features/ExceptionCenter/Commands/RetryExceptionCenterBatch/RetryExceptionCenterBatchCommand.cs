using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterBatch;

/// <summary>
/// Retries every item in one root-cause group. When a posting period was closed for
/// an afternoon, the fix is one SAP change and one retry of all forty documents —
/// not forty clicks.
/// </summary>
public sealed record RetryExceptionCenterBatchCommand(IReadOnlyList<ExceptionCenterItemRefDto> Items)
    : IRequest<ErrorOr<ExceptionCenterBatchRetryResultDto>>;
