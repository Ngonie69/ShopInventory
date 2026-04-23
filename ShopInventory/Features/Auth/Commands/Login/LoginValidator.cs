using FluentValidation;

namespace ShopInventory.Features.Auth.Commands.Login;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Username)
            .Must(username => !string.IsNullOrWhiteSpace(username))
            .WithMessage("Username is required");

        RuleFor(x => x.Password)
            .Must(password => !string.IsNullOrWhiteSpace(password))
            .WithMessage("Password is required");
    }
}