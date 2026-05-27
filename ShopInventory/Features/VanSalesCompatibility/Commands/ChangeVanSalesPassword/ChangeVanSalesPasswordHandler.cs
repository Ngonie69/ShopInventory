using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Features.Users.Commands.AdminChangePassword;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ChangeVanSalesPassword;

public sealed class ChangeVanSalesPasswordHandler(
    IMediator mediator
) : IRequestHandler<ChangeVanSalesPasswordCommand, ErrorOr<string>>
{
    public async Task<ErrorOr<string>> Handle(
        ChangeVanSalesPasswordCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Request.Password))
        {
            return Error.Validation(
                "VanSalesCompatibility.PasswordRequired",
                "A new password is required.");
        }

        var result = await mediator.Send(
            new AdminChangePasswordCommand(
                command.UserId,
                new AdminChangePasswordRequest
                {
                    NewPassword = command.Request.Password.Trim()
                }),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        return "Password changed successfully";
    }
}