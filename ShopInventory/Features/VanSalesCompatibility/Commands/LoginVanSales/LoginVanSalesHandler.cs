using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Commands.Login;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.LoginVanSales;

public sealed class LoginVanSalesHandler(
    IMediator mediator,
    ApplicationDbContext db
) : IRequestHandler<LoginVanSalesCommand, ErrorOr<VanSalesLoginResponse>>
{
    public async Task<ErrorOr<VanSalesLoginResponse>> Handle(
        LoginVanSalesCommand command,
        CancellationToken cancellationToken)
    {
        var authResult = await mediator.Send(
            new LoginCommand(command.Request.Username, command.Request.Password, command.IpAddress),
            cancellationToken);

        if (authResult.IsError)
        {
            return authResult.Errors;
        }

        var authResponse = authResult.Value;
        if (authResponse.RequiresTwoFactor)
        {
            return Error.Validation(
                "VanSalesCompatibility.TwoFactorUnsupported",
                "Two-factor authentication is not supported by the van sales compatibility login.");
        }

        if (authResponse.User is null)
        {
            return Error.Unexpected(
                code: "VanSalesCompatibility.MissingUserInfo",
                description: "Authenticated response did not include user details.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == authResponse.User.Username, cancellationToken);

        if (user is null)
        {
            return Error.NotFound("VanSalesCompatibility.UserNotFound", "Authenticated user was not found.");
        }

        var shopsResult = await mediator.Send(new GetVanSalesCustomersQuery(user.Id), cancellationToken);
        if (shopsResult.IsError)
        {
            return shopsResult.Errors;
        }

        return VanSalesCompatibilityMapper.MapLoginResponse(authResponse, user, shopsResult.Value);
    }
}