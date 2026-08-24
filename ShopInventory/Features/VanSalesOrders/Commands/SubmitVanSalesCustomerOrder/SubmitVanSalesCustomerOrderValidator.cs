using FluentValidation;

namespace ShopInventory.Features.VanSalesOrders.Commands.SubmitVanSalesCustomerOrder;

public sealed class SubmitVanSalesCustomerOrderValidator
    : AbstractValidator<SubmitVanSalesCustomerOrderCommand>
{
    public SubmitVanSalesCustomerOrderValidator()
    {
        // Required, not optional-with-a-fallback. An order arriving without a key cannot be made
        // idempotent after the fact, and a server-generated one would be different on every retry —
        // which is exactly the duplicate this whole design exists to prevent.
        RuleFor(x => x.ClientRequestId)
            .NotEmpty().WithMessage("A client request id is required.")
            .MaximumLength(100);

        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("Add at least one item before sending your order.");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemCode)
                .NotEmpty().WithMessage("Each line needs an item.")
                .MaximumLength(50);

            line.RuleFor(l => l.Quantity)
                .GreaterThan(0).WithMessage("Quantity must be more than zero.")
                .LessThanOrEqualTo(100_000).WithMessage("That quantity is too large.");
        });

        RuleFor(x => x.CustomerNotes).MaximumLength(1000);
        RuleFor(x => x.DeviceInfo).MaximumLength(200);
        RuleFor(x => x.AppVersion).MaximumLength(50);
    }
}
