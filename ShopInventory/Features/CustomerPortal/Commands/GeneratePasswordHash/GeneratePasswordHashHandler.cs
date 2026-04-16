using ShopInventory.DTOs;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;

namespace ShopInventory.Features.CustomerPortal.Commands.GeneratePasswordHash;

public sealed class GeneratePasswordHashHandler(
    IWebHostEnvironment environment
) : IRequestHandler<GeneratePasswordHashCommand, ErrorOr<PasswordHashResponse>>
{
    public Task<ErrorOr<PasswordHashResponse>> Handle(
        GeneratePasswordHashCommand command,
        CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return Task.FromResult<ErrorOr<PasswordHashResponse>>(Errors.CustomerPortal.DevOnlyEndpoint);

        if (string.IsNullOrEmpty(command.Password))
            return Task.FromResult<ErrorOr<PasswordHashResponse>>(Errors.CustomerPortal.WeakPassword);

        if (!IsPasswordStrong(command.Password))
            return Task.FromResult<ErrorOr<PasswordHashResponse>>(Errors.CustomerPortal.WeakPassword);

        var hash = BCrypt.Net.BCrypt.HashPassword(command.Password, 12);

        var response = new PasswordHashResponse
        {
            PasswordHash = hash,
            Message = "Use this hash in the CustomerPortalUsers table"
        };

        return Task.FromResult<ErrorOr<PasswordHashResponse>>(response);
    }

    private static bool IsPasswordStrong(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return false;

        bool hasUpper = password.Any(char.IsUpper);
        bool hasLower = password.Any(char.IsLower);
        bool hasDigit = password.Any(char.IsDigit);
        bool hasSpecial = password.Any(c => !char.IsLetterOrDigit(c));

        return hasUpper && hasLower && hasDigit && hasSpecial;
    }
}
