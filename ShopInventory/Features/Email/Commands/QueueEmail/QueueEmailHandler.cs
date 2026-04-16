using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Email.Commands.QueueEmail;

public sealed class QueueEmailHandler(
    IEmailService emailService,
    ILogger<QueueEmailHandler> logger
) : IRequestHandler<QueueEmailCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        QueueEmailCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await emailService.QueueEmailAsync(command.Request, command.Category, cancellationToken);
            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error queuing email");
            return Errors.Email.QueueFailed(ex.Message);
        }
    }
}
