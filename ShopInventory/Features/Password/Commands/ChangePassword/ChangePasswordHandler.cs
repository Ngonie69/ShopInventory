using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Password.Commands.ChangePassword;

public sealed class ChangePasswordHandler(
    IPasswordResetService passwordResetService,
    ILogger<ChangePasswordHandler> logger
) : IRequestHandler<ChangePasswordCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        ChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var userId = command.UserId;

            // If no user ID from JWT, try resolving by username (API key auth from web app)
            if (userId == null && !string.IsNullOrWhiteSpace(command.Username))
            {
                userId = await passwordResetService.GetUserIdByUsernameAsync(command.Username);
            }

            if (userId == null)
                return Errors.Password.Unauthenticated;

            var result = await passwordResetService.ChangePasswordAsync(
                userId.Value, command.CurrentPassword, command.NewPassword);

            if (!result.IsSuccess)
                return Errors.Password.ChangeFailed(result.Message);

            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error changing password");
            return Errors.Password.ChangeFailed(ex.Message);
        }
    }
}
