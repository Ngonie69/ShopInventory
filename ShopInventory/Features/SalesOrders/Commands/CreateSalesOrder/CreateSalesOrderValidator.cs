using FluentValidation;

namespace ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;

public sealed class CreateSalesOrderValidator : AbstractValidator<CreateSalesOrderCommand>
{
    public CreateSalesOrderValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty().WithMessage("User ID is required");

        RuleFor(x => x.Request).NotNull().WithMessage("Request body is required");

        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.CardCode)
                .NotEmpty().WithMessage("Customer code is required")
                .MaximumLength(50).WithMessage("Customer code must not exceed 50 characters");

            RuleFor(x => x.Request.DiscountPercent)
                .InclusiveBetween(0, 100).WithMessage("Discount percent must be between 0 and 100");

            RuleFor(x => x.Request.Lines)
                .NotEmpty().WithMessage("At least one line item is required");

            RuleForEach(x => x.Request.Lines).ChildRules(line =>
            {
                line.RuleFor(l => l.ItemCode)
                    .NotEmpty().WithMessage("Item code is required");

                line.RuleFor(l => l.Quantity)
                    .GreaterThan(0).WithMessage("Quantity must be greater than zero");

                line.RuleFor(l => l.UnitPrice)
                    .GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative");

                line.RuleFor(l => l.DiscountPercent)
                    .InclusiveBetween(0, 100).WithMessage("Line discount percent must be between 0 and 100");
            });
        });
    }
}
