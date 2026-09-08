using FluentValidation;

namespace ShopInventory.Features.Invoices.Commands.CancelInvoice;

public sealed class CancelInvoiceValidator : AbstractValidator<CancelInvoiceCommand>
{
    public CancelInvoiceValidator()
    {
        RuleFor(command => command.DocEntry)
            .GreaterThan(0)
            .WithMessage("A valid invoice document entry is required.");

        RuleFor(command => command.Reason)
            .NotEmpty()
            .WithMessage("A cancellation reason is required.")
            // The SAP field is alpha(250). Whether the value is one SAP accepts is decided in the
            // handler against the company database's own list; this is only the field's width.
            .MaximumLength(250)
            .WithMessage("A cancellation reason cannot be longer than 250 characters.");

        RuleFor(command => command.Comments)
            .MaximumLength(500)
            .WithMessage("Comments cannot be longer than 500 characters.");
    }
}
