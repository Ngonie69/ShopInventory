using FluentValidation;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// The raised-date and originator narrowing the list and the counts share. Both go into SAP's OData
/// filter, the dates as day literals and the originator as an int.
/// </summary>
public sealed class CreditNoteApprovalRequestFilterValidator : AbstractValidator<CreditNoteApprovalRequestFilter>
{
    public CreditNoteApprovalRequestFilterValidator()
    {
        RuleFor(filter => filter.RaisedTo)
            .GreaterThanOrEqualTo(filter => filter.RaisedFrom)
            .When(filter => filter.RaisedFrom is not null && filter.RaisedTo is not null)
            .WithMessage("The raised-to date must not be before the raised-from date.");

        RuleFor(filter => filter.OriginatorId)
            .GreaterThan(0)
            .When(filter => filter.OriginatorId is not null)
            .WithMessage("The originator must be a SAP user key.");
    }
}
