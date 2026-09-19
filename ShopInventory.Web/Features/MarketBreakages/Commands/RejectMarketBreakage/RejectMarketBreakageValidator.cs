using FluentValidation;

namespace ShopInventory.Web.Features.MarketBreakages.Commands.RejectMarketBreakage;

public sealed class RejectMarketBreakageValidator : AbstractValidator<RejectMarketBreakageCommand>
{
    public RejectMarketBreakageValidator()
    {
        RuleFor(command => command.Id).GreaterThan(0);
        RuleFor(command => command.Remarks)
            .NotEmpty().WithMessage("Say why the report is rejected — the rep sees it.")
            .MaximumLength(500);
    }
}
