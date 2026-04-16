using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Password.Commands.UpdateCredentials;

public sealed class UpdateCredentialsHandler(
    IPasswordResetService passwordResetService,
    ILogger<UpdateCredentialsHandler> logger
) : IRequestHandler<UpdateCredentialsCommand, ErrorOr<UpdateCredentialsResponse>>
{
    public async Task<ErrorOr<UpdateCredentialsResponse>> Handle(
        UpdateCredentialsCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await passwordResetService.UpdateCredentialsAsync(
                command.UserId, command.Request);

            if (!result.IsSuccess)
                return Errors.Password.UpdateFailed(result.Message);

            return result.Data!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating credentials for user {UserId}", command.UserId);
            return Errors.Password.UpdateFailed(ex.Message);
        }
    }
}
