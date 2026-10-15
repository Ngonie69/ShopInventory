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
            var result = await passwordResetService.ChangePasswordAsync(
                command.UserId, command.CurrentPassword, command.NewPassword);

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
