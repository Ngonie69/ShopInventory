using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;

/// <summary>One approval request with the draft it holds, its lines, attachments, approver lines and stage.</summary>
public sealed record GetCreditNoteApprovalQuery(int Code) : IRequest<ErrorOr<CreditNoteApprovalDetailDto>>;
