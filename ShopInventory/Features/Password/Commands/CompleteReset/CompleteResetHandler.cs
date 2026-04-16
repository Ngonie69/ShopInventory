using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Password.Commands.CompleteReset;

public sealed class CompleteResetHandler(
    IPasswordResetService passwordResetService,
    ILogger<CompleteResetHandler> logger
) : IRequestHandler<CompleteResetCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        CompleteResetCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await passwordResetService.CompleteResetAsync(
                command.Token, command.NewPassword, command.ClientIp);

            if (!result.IsSuccess)
                return Errors.Password.ResetFailed(result.Message);

            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error completing password reset");
            return Errors.Password.ResetFailed(ex.Message);
        }
    }
}
