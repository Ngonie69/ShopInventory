using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Password.Commands.RequestReset;

public sealed class RequestResetHandler(
    IPasswordResetService passwordResetService,
    ILogger<RequestResetHandler> logger
) : IRequestHandler<RequestResetCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        RequestResetCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await passwordResetService.InitiateResetAsync(command.Email, command.ClientIp);

            // Always return success to prevent email enumeration
            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error initiating password reset for {Email}", command.Email);
            // Still return success to prevent enumeration
            return "If the email exists, a reset link has been sent.";
        }
    }
}
