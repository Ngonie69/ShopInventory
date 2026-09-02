using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;

/// <summary>
/// Converts the approved draft behind a SAP approval request into the posted credit note, then
/// projects and fiscalises it the way a credit note this app creates is.
/// </summary>
public sealed record AddApprovedCreditNoteCommand(
    int Code,
    Guid UserId,
    string Username,
    string? ClientRequestId) : IRequest<ErrorOr<AddApprovedCreditNoteResultDto>>;
