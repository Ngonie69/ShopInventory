using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;

/// <summary>Turn an approved inventory transfer draft into the real transfer in SAP.</summary>
/// <param name="DraftEntry">The draft's DocEntry.</param>
/// <param name="UserId">Who is adding it; their role and warehouses decide whether they may.</param>
/// <param name="Username">For the audit trail.</param>
/// <param name="ClientRequestId">
/// The caller's idempotency key. Recorded only: the add is claimed per draft, whoever clicks and
/// whatever key they send, because a draft is added exactly once.
/// </param>
/// <param name="CallerAuthorized">
/// True only when the caller has already decided this user may act: a factory transfer's own add, where
/// the person who may post the transfer (<c>EnsureCanActOnSourceAsync</c>) adds the draft it was saved
/// as, and a conversion, which asked <c>EnsureCanConvertRequestAsync</c> on the request the draft was
/// made from (the wash bay converts from any warehouse, wider than its page scope). Never set from the
/// SAP-held transfers page, which asks the page's own scope.
/// </param>
public sealed record AddApprovedSapTransferCommand(
    int DraftEntry,
    Guid UserId,
    string Username,
    string? ClientRequestId,
    bool CallerAuthorized = false)
    : IRequest<ErrorOr<AddApprovedSapTransferResultDto>>;
