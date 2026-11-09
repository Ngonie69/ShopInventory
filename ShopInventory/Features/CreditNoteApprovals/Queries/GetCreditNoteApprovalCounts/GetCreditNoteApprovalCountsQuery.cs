using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;

/// <summary>How many requests are pending, approved and rejected, in the caller's stage scope.</summary>
/// <param name="CallerUserId">
/// The caller's account, whose role decides which SAP stages are counted — see
/// <see cref="ICreditNoteApprovalStageScope"/>. Null is a service caller and counts every stage.
/// </param>
/// <param name="Filter">
/// The same raised dates and originator the list is narrowed by, so a tile counts what it opens.
/// </param>
public sealed record GetCreditNoteApprovalCountsQuery(Guid? CallerUserId = null, CreditNoteApprovalRequestFilter? Filter = null)
    : IRequest<ErrorOr<CreditNoteApprovalCountsDto>>;
