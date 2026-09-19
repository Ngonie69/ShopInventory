using FluentValidation;

namespace ShopInventory.Features.MarketBreakages.Commands.ReportMarketBreakage;

public sealed class ReportMarketBreakageValidator : AbstractValidator<ReportMarketBreakageCommand>
{
    public const int MaxLines = 100;

    public ReportMarketBreakageValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();

        RuleFor(command => command.Request.ClientRequestId)
            .NotEmpty().WithMessage("client_request_id is required.")
            .MaximumLength(100);

        RuleFor(command => command.Request.CardCode).MaximumLength(50);
        RuleFor(command => command.Request.CardName).MaximumLength(200);
        RuleFor(command => command.Request.Remarks).MaximumLength(500);

        RuleFor(command => command.Request.Items)
            .NotEmpty().WithMessage("At least one product is required.")
            .Must(items => items.Count <= MaxLines).WithMessage($"A report may carry at most {MaxLines} products.");

        RuleForEach(command => command.Request.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.Code).NotEmpty().MaximumLength(50);
            item.RuleFor(line => line.Description).MaximumLength(200);
            item.RuleFor(line => line.Reason).MaximumLength(100);
            item.RuleFor(line => line.Quantity)
                .GreaterThan(0).WithMessage("Each quantity must be greater than zero.")
                .LessThanOrEqualTo(100_000m);
        });
    }
}
