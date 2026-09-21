using FluentValidation;

namespace ShopInventory.Web.Features.SapUserAccounts.Commands.ChangeSapUserPassword;

/// <summary>
/// The same bounds the API applies, checked before a round trip is spent on them. SAP's own password
/// policy is stricter on most companies and is the authority — a password that passes here can still
/// be refused, and SAP's reason is what the dialog then shows.
/// </summary>
public sealed class ChangeSapUserPasswordValidator : AbstractValidator<ChangeSapUserPasswordCommand>
{
    public ChangeSapUserPasswordValidator()
    {
        RuleFor(command => command.InternalKey).GreaterThan(0);

        RuleFor(command => command.NewPassword)
            .NotEmpty().WithMessage("A new password is required.")
            .MinimumLength(8).WithMessage("A SAP password must be at least 8 characters.")
            .MaximumLength(32).WithMessage("A SAP password cannot be longer than 32 characters.")
            .Must(password => password is not null && password.Trim().Length == password.Length)
                .WithMessage("A SAP password cannot start or end with a space.");
    }
}
