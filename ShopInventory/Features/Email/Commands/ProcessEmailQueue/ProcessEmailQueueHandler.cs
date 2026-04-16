using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Email.Commands.ProcessEmailQueue;

public sealed class ProcessEmailQueueHandler(
    IEmailService emailService,
    ILogger<ProcessEmailQueueHandler> logger
) : IRequestHandler<ProcessEmailQueueCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ProcessEmailQueueCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await emailService.ProcessEmailQueueAsync(cancellationToken);
            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing email queue");
            return Errors.Email.ProcessingFailed(ex.Message);
        }
    }
}
