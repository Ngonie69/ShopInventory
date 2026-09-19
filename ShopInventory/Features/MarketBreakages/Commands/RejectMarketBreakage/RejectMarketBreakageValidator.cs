using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Commands.RejectMarketBreakage;

public sealed class RejectMarketBreakageValidator : AbstractValidator<RejectMarketBreakageCommand>
{
    public RejectMarketBreakageValidator()
    {
        RuleFor(command => command.BreakageId).GreaterThan(0);
        RuleFor(command => command.UserId).NotEmpty();

        // A rejected report tells the rep nothing moved; the reason is the only thing that says why.
        RuleFor(command => command.Remarks)
            .NotEmpty().WithMessage("Say why the report is rejected.")
            .MaximumLength(500);
    }
}
