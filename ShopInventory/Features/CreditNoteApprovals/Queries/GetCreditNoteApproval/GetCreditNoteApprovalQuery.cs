using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;

/// <summary>One approval request with the draft it holds, its lines, attachments, approver lines and stage.</summary>
/// <param name="CallerUserId">The caller's account; a stage-scoped one is refused a request outside its stages.</param>
public sealed record GetCreditNoteApprovalQuery(int Code, Guid? CallerUserId = null) : IRequest<ErrorOr<CreditNoteApprovalDetailDto>>;
