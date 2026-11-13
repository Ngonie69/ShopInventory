using FluentValidation;

namespace ShopInventory.Features.VendingCashUps.Commands.QueryVendingCashUp;

public sealed class QueryVendingCashUpValidator : AbstractValidator<QueryVendingCashUpCommand>
{
    public QueryVendingCashUpValidator()
    {
        RuleFor(command => command.CashUpId).GreaterThan(0);
        RuleFor(command => command.Note).MaximumLength(500);
    }
}
