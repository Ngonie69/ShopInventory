using FluentValidation;

namespace ShopInventory.Web.Features.MarketBreakages.Commands.ConfirmMarketBreakage;

public sealed class ConfirmMarketBreakageValidator : AbstractValidator<ConfirmMarketBreakageCommand>
{
    public const int MaxRemarksLength = 500;

    public ConfirmMarketBreakageValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
        RuleFor(command => command.Remarks).MaximumLength(MaxRemarksLength);
        RuleFor(command => command.Lines).NotEmpty();
        RuleForEach(command => command.Lines).ChildRules(line =>
            line.RuleFor(item => item.ConfirmedQuantity)
                .GreaterThanOrEqualTo(0).WithMessage("A counted quantity cannot be negative."));
        RuleFor(command => command.Lines)
            .Must(lines => lines.Any(line => line.ConfirmedQuantity > 0))
            .When(command => command.Lines.Count > 0)
            .WithMessage("Every count is zero, so there is nothing to transfer. Reject the report instead.");
    }
}
