using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.RetryDesktopSaleFiscalisation;

public sealed class RetryDesktopSaleFiscalisationValidator : AbstractValidator<RetryDesktopSaleFiscalisationCommand>
{
    public RetryDesktopSaleFiscalisationValidator()
    {
        RuleFor(command => command.CallerUserId)
            .NotEmpty()
            .WithMessage("The retry could not be attributed to a signed-in user.");

        RuleFor(command => command.ExternalReferenceId)
            .NotEmpty()
            .WithMessage("Name the sale to fiscalise.")
            .MaximumLength(100);
    }
}
