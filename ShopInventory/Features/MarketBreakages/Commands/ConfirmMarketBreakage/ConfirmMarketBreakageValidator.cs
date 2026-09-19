using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Commands.ConfirmMarketBreakage;

public sealed class ConfirmMarketBreakageValidator : AbstractValidator<ConfirmMarketBreakageCommand>
{
    public ConfirmMarketBreakageValidator()
    {
        RuleFor(command => command.BreakageId).GreaterThan(0);
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.Remarks).MaximumLength(500);

        RuleFor(command => command.Lines)
            .NotEmpty().WithMessage("Enter the confirmed quantity for each line.")
            .Must(lines => lines.Select(line => line.LineId).Distinct().Count() == lines.Count)
            .WithMessage("Each line may be confirmed only once.");

        RuleForEach(command => command.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.LineId).GreaterThan(0);
            line.RuleFor(item => item.ConfirmedQuantity)
                .GreaterThanOrEqualTo(0).WithMessage("A confirmed quantity cannot be negative.")
                .LessThanOrEqualTo(100_000m);
        });
    }
}
