using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterBatch;

public sealed record RetryExceptionCenterBatchCommand(IReadOnlyList<ExceptionCenterItemRefModel> Items)
    : IRequest<ErrorOr<ExceptionCenterBatchRetryResultModel>>;
