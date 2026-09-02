using FluentValidation;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;

public sealed class DecideCreditNoteApprovalValidator : AbstractValidator<DecideCreditNoteApprovalCommand>
{
    /// <summary>
    /// What the person may type. SAP's remark column takes 254 characters and the prefix naming the
    /// person has to fit in front of it, so the free text is capped well below that.
    /// </summary>
    public const int MaxRemarksLength = 150;

    public DecideCreditNoteApprovalValidator()
    {
        RuleFor(command => command.Code).GreaterThan(0);

        RuleFor(command => command.Decision)
            .Must(decision =>
                string.Equals(decision, ApprovalDecisionValues.Approved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(decision, ApprovalDecisionValues.NotApproved, StringComparison.OrdinalIgnoreCase))
            .WithMessage("Decision must be Approved or NotApproved.");

        RuleFor(command => command.Remarks).MaximumLength(MaxRemarksLength);
        RuleFor(command => command.Username).NotEmpty();
    }
}
