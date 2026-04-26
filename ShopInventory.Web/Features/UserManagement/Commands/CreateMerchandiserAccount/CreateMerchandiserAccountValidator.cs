using FluentValidation;

namespace ShopInventory.Web.Features.UserManagement.Commands.CreateMerchandiserAccount;

public sealed class CreateMerchandiserAccountValidator : AbstractValidator<CreateMerchandiserAccountCommand>
{
    public CreateMerchandiserAccountValidator()
    {
        RuleFor(command => command.Request.Username)
            .NotEmpty()
            .MinimumLength(3)
            .MaximumLength(50);

        RuleFor(command => command.Request.Email)
            .NotEmpty()
            .EmailAddress();

        RuleFor(command => command.Request.Password)
            .NotEmpty()
            .MinimumLength(8);

        RuleFor(command => command.Request.ConfirmPassword)
            .Equal(command => command.Request.Password)
            .WithMessage("Passwords do not match.");

        RuleFor(command => command.Request.AssignedCustomerCodes)
            .Must(customerCodes => customerCodes.Count > 0)
            .WithMessage("At least one customer is required for a merchandiser account.");
    }
}