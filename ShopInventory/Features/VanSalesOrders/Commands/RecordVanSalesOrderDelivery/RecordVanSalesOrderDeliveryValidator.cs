using FluentValidation;

namespace ShopInventory.Features.VanSalesOrders.Commands.RecordVanSalesOrderDelivery;

public sealed class RecordVanSalesOrderDeliveryValidator
    : AbstractValidator<RecordVanSalesOrderDeliveryCommand>
{
    public RecordVanSalesOrderDeliveryValidator()
    {
        RuleFor(x => x.OrderId).GreaterThan(0);

        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("Record what was delivered on at least one line.");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineNumber).GreaterThan(0);

            // Zero is meaningful — it is how a line that could not be filled at all is recorded —
            // so only negatives are refused.
            line.RuleFor(l => l.QuantityFulfilled)
                .GreaterThanOrEqualTo(0).WithMessage("A delivered quantity cannot be negative.");
        });
    }
}
