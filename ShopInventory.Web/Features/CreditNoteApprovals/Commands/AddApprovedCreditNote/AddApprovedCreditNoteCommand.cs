using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;

/// <summary>Adds the approved draft, first changing the line quantities given, if any.</summary>
public sealed record AddApprovedCreditNoteCommand(
    int Code,
    string ClientRequestId,
    IReadOnlyList<CreditNoteLineQuantityDto>? LineQuantities = null)
    : IRequest<ErrorOr<AddApprovedCreditNoteResultDto>>;
