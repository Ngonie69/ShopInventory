using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;

/// <summary>
/// Converts the approved draft behind a SAP approval request into the posted credit note, then
/// projects and fiscalises it the way a credit note this app creates is.
/// </summary>
/// <remarks>
/// <c>LineQuantities</c> are new quantities for draft lines, written to the draft just before the
/// add. Null or empty adds the draft as it was approved.
/// </remarks>
public sealed record AddApprovedCreditNoteCommand(
    int Code,
    Guid UserId,
    string Username,
    string? ClientRequestId,
    IReadOnlyList<CreditNoteLineQuantityDto>? LineQuantities = null) : IRequest<ErrorOr<AddApprovedCreditNoteResultDto>>;
