using ShopInventory.DTOs;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerPortal.Commands.RegisterCustomer;

public sealed class RegisterCustomerHandler(
    IAuditService auditService,
    ILogger<RegisterCustomerHandler> logger
) : IRequestHandler<RegisterCustomerCommand, ErrorOr<CustomerRegistrationResponse>>
{
    public async Task<ErrorOr<CustomerRegistrationResponse>> Handle(
        RegisterCustomerCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsPasswordStrong(command.Password))
                return Errors.CustomerPortal.WeakPassword;

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(command.Password, 12);

            var response = new CustomerRegistrationResponse
            {
                Success = true,
                CardCode = command.CardCode,
                Email = command.Email,
                Message = "Customer registered successfully."
            };

            logger.LogInformation("Generated registration for customer {CardCode}", command.CardCode);

            try { await auditService.LogAsync(AuditActions.RegisterCustomer, "CustomerPortalUser", command.CardCode, $"Customer {command.CardCode} registered with email {command.Email}", true); } catch { }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering customer {CardCode}", command.CardCode);
            return Errors.CustomerPortal.RegistrationFailed(ex.Message);
        }
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
