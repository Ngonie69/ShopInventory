using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.UpdatePostingDatePolicy;

public sealed class UpdatePostingDatePolicyValidator : AbstractValidator<UpdatePostingDatePolicyCommand>
{
    public UpdatePostingDatePolicyValidator()
    {
        RuleFor(x => x.CallerUserId).NotEmpty();
    }
}
