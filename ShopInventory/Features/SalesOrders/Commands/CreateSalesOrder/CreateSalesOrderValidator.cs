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

            RuleFor(x => x.Request.ClientRequestId)
                .MaximumLength(100).WithMessage("Client request ID must not exceed 100 characters")
                .When(x => !string.IsNullOrWhiteSpace(x.Request?.ClientRequestId));

            // Required for every source, not just Mobile, and the difference matters because of how
            // this endpoint is reached. IdempotencyMiddleware refuses a keyless POST /api/salesorder
            // with 428 — except for merchandiser, sales rep, ADR and sales roles, whose older mobile
            // clients send the key in the body rather than the header. That exemption is granted by
            // *role*, while the rule that made it safe was written by *source*: one of those users
            // posting an order that is not Source=Mobile passed the gate carrying no key at all, and
            // CreateAsync then had nothing to deduplicate on. A resubmission became a second order,
            // and a second SAP document once it was approved.
            //
            // The controller folds an Idempotency-Key header into ClientRequestId before this runs,
            // so a caller supplying either one passes. Only a caller supplying neither is refused —
            // and that caller has no duplicate protection at all today.
            RuleFor(x => x.Request.ClientRequestId)
                .NotEmpty()
                .WithMessage(
                    "Client request ID is required. Send it as clientRequestId in the body or as an "
                    + "Idempotency-Key header, so a retried submission cannot become a second order.");

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
