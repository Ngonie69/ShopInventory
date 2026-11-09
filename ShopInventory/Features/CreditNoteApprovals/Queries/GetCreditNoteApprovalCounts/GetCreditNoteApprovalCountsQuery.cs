using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

/// <summary>How many requests are pending, approved and rejected, in the caller's stage scope.</summary>
/// <param name="CallerUserId">
/// The caller's account, whose role decides which SAP stages are counted — see
/// <see cref="ICreditNoteApprovalStageScope"/>. Null is a service caller and counts every stage.
/// </param>
public sealed record GetCreditNoteApprovalCountsQuery(Guid? CallerUserId = null)
    : IRequest<ErrorOr<CreditNoteApprovalCountsDto>>;
