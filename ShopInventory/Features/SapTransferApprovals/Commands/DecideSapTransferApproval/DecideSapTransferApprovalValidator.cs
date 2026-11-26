using FluentValidation;
using ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.SapTransferApprovals.Commands.DecideSapTransferApproval;

public sealed class DecideSapTransferApprovalValidator : AbstractValidator<DecideSapTransferApprovalCommand>
{
    public DecideSapTransferApprovalValidator()
    {
        RuleFor(command => command.Code).GreaterThan(0);

        RuleFor(command => command.Decision)
            .Must(decision =>
                string.Equals(decision, ApprovalDecisionValues.Approved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision, ApprovalDecisionValues.NotApproved, StringComparison.OrdinalIgnoreCase))
            .WithMessage("Decision must be Approved or NotApproved.");

        // The same SAP remark column as a credit memo decision, with the same prefix in front.
        RuleFor(command => command.Remarks).MaximumLength(DecideCreditNoteApprovalValidator.MaxRemarksLength);
        RuleFor(command => command.Username).NotEmpty();
    }
}
