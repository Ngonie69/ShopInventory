using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterBatch;

public sealed class RetryExceptionCenterBatchHandler(
    IExceptionCenterService exceptionCenterService,
    ILogger<RetryExceptionCenterBatchHandler> logger
) : IRequestHandler<RetryExceptionCenterBatchCommand, ErrorOr<ExceptionCenterBatchRetryResultModel>>
{
    public async Task<ErrorOr<ExceptionCenterBatchRetryResultModel>> Handle(
        RetryExceptionCenterBatchCommand request,
        CancellationToken cancellationToken)
    {
        if (request.Items.Count == 0)
        {
            return Errors.ExceptionCenter.RetryFailed("There is nothing in this group that can be retried.");
        }

        try
        {
            var result = await exceptionCenterService.RetryBatchAsync(request.Items, cancellationToken);
            return result is null
                ? Errors.ExceptionCenter.RetryFailed("Batch retry request was rejected.")
                : result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error batch-retrying {Count} exception center items", request.Items.Count);
            return Errors.ExceptionCenter.RetryFailed("Batch retry request was rejected.");
        }
    }
}
