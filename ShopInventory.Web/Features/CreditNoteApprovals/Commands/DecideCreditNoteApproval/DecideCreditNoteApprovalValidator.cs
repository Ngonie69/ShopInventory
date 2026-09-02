using FluentValidation;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;

public sealed class DecideCreditNoteApprovalValidator : AbstractValidator<DecideCreditNoteApprovalCommand>
{
    /// <summary>Matches the API's cap: SAP's remark column takes 254 and the prefix naming the person sits in front.</summary>
    public const int MaxRemarksLength = 150;

    public DecideCreditNoteApprovalValidator()
    {
        RuleFor(command => command.Code).GreaterThan(0);
        RuleFor(command => command.Decision)
            .Must(decision => decision is "Approved" or "NotApproved")
            .WithMessage("Decision must be Approved or NotApproved.");
        RuleFor(command => command.Remarks).MaximumLength(MaxRemarksLength);
        RuleFor(command => command.ClientRequestId).NotEmpty();
    }
}
