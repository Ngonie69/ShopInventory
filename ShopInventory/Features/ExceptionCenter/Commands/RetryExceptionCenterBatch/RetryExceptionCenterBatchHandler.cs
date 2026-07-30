using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;

namespace ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterBatch;

public sealed class RetryExceptionCenterBatchHandler(
    IMediator mediator,
    ILogger<RetryExceptionCenterBatchHandler> logger
) : IRequestHandler<RetryExceptionCenterBatchCommand, ErrorOr<ExceptionCenterBatchRetryResultDto>>
{
    private const int MaxBatchSize = 200;

    public async Task<ErrorOr<ExceptionCenterBatchRetryResultDto>> Handle(
        RetryExceptionCenterBatchCommand command,
        CancellationToken cancellationToken)
    {
        var requested = command.Items?
            .Where(item => !string.IsNullOrWhiteSpace(item.Source) && item.ItemId > 0)
            .GroupBy(item => $"{item.Source}:{item.ItemId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList() ?? [];

        if (requested.Count == 0)
        {
            return Errors.ExceptionCenter.UpdateFailed("BatchRetry", "No retryable items were supplied.");
        }

        if (requested.Count > MaxBatchSize)
        {
            return Errors.ExceptionCenter.UpdateFailed(
                "BatchRetry",
                $"A batch retry is limited to {MaxBatchSize} items; {requested.Count} were supplied.");
        }

        var result = new ExceptionCenterBatchRetryResultDto { RequestedCount = requested.Count };

        // Retried one at a time and on purpose: each source has its own requeue rules,
        // and one item that cannot be retried must not stop the rest of the group.
        foreach (var item in requested)
        {
            var outcome = await mediator.Send(
                new RetryExceptionCenterItemCommand(item.Source, item.ItemId),
                cancellationToken);

            if (outcome.IsError)
            {
                result.FailedCount++;
                if (result.Failures.Count < 10)
                {
                    result.Failures.Add($"{item.Source}:{item.ItemId} — {outcome.FirstError.Description}");
                }

                continue;
            }

            result.RetriedCount++;
        }

        logger.LogInformation(
            "Exception center batch retry requeued {Retried} of {Requested} items ({Failed} failed)",
            result.RetriedCount, result.RequestedCount, result.FailedCount);

        return result;
    }
}
