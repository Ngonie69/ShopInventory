using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;

public sealed class PostDesktopSaleToSapValidator : AbstractValidator<PostDesktopSaleToSapCommand>
{
    public PostDesktopSaleToSapValidator()
    {
        RuleFor(command => command.CallerUserId)
            .NotEmpty()
            .WithMessage("The post could not be attributed to a signed-in user.");

        RuleFor(command => command.ExternalReferenceId)
            .NotEmpty()
            .WithMessage("Name the sale to post.")
            // The column it is matched against, so a longer string cannot name a sale that exists.
            .MaximumLength(100);
    }
}
