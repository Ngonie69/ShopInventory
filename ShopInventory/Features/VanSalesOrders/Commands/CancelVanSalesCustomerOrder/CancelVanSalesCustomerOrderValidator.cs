using FluentValidation;

namespace ShopInventory.Features.VanSalesOrders.Commands.CancelVanSalesCustomerOrder;

public sealed class CancelVanSalesCustomerOrderValidator
    : AbstractValidator<CancelVanSalesCustomerOrderCommand>
{
    public CancelVanSalesCustomerOrderValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}
