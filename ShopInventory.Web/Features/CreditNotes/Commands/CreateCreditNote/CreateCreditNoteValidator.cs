using FluentValidation;

namespace ShopInventory.Web.Features.CreditNotes.Commands.CreateCreditNote;

public sealed class CreateCreditNoteValidator : AbstractValidator<CreateCreditNoteCommand>
{
    public CreateCreditNoteValidator()
    {
        RuleFor(command => command.Request.CardCode)
            .NotEmpty()
            .WithMessage("Please select a customer.");

        RuleFor(command => command.Request.Reason)
            .NotEmpty()
            .WithMessage("Please provide a reason for the credit note.");

        RuleFor(command => command.Request.Lines)
            .NotEmpty()
            .WithMessage("Add at least one credit note line.");

        RuleForEach(command => command.Request.Lines).ChildRules(line =>
        {
            line.RuleFor(item => item.ItemCode).NotEmpty().WithMessage("Item code is required.");
            line.RuleFor(item => item.Quantity).GreaterThan(0).WithMessage("Quantity must be greater than zero.");
            line.RuleFor(item => item.UnitPrice).GreaterThanOrEqualTo(0).WithMessage("Unit price cannot be negative.");
        });
    }
}