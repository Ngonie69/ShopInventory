using FluentValidation;

namespace ShopInventory.Features.DesktopIntegration.Commands.BackfillFiscalTransactions;

public sealed class BackfillFiscalTransactionsValidator : AbstractValidator<BackfillFiscalTransactionsCommand>
{
    public BackfillFiscalTransactionsValidator()
    {
        RuleFor(command => command.Request)
            .NotNull();

        RuleFor(command => command.Request.MaxInvoices)
            .InclusiveBetween(1, 1000);

        RuleFor(command => command.Request.PageSize)
            .InclusiveBetween(1, 200);

        RuleFor(command => command.Request)
            .Must(request => !request.FromUtc.HasValue || !request.ToUtc.HasValue || request.FromUtc.Value.Date <= request.ToUtc.Value.Date)
            .WithMessage("FromUtc must be on or before ToUtc.");
    }
}