using ShopInventory.DTOs;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;

namespace ShopInventory.Features.CustomerPortal.Commands.BulkRegisterCustomers;

public sealed class BulkRegisterCustomersHandler
    : IRequestHandler<BulkRegisterCustomersCommand, ErrorOr<BulkRegistrationResponse>>
{
    public Task<ErrorOr<BulkRegistrationResponse>> Handle(
        BulkRegisterCustomersCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(command.DefaultPassword))
            return Task.FromResult<ErrorOr<BulkRegistrationResponse>>(
                Errors.CustomerPortal.WeakPassword);

        if (!IsPasswordStrong(command.DefaultPassword))
            return Task.FromResult<ErrorOr<BulkRegistrationResponse>>(
                Errors.CustomerPortal.WeakPassword);

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(command.DefaultPassword, 12);

        var results = new List<CustomerRegistrationResponse>();

        foreach (var customer in command.Customers)
        {
            results.Add(new CustomerRegistrationResponse
            {
                Success = true,
                CardCode = customer.CardCode,
                Email = customer.Email,
                Message = "Registration generated successfully"
            });
        }

        var response = new BulkRegistrationResponse
        {
            Success = true,
            Count = results.Count,
            Message = $"Generated {results.Count} customer registrations",
            Customers = results
        };

        return Task.FromResult<ErrorOr<BulkRegistrationResponse>>(response);
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
