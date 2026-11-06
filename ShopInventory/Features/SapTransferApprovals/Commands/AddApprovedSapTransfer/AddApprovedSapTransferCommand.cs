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
public sealed record AddApprovedSapTransferCommand(int DraftEntry, Guid UserId, string Username, string? ClientRequestId)
    : IRequest<ErrorOr<AddApprovedSapTransferResultDto>>;
