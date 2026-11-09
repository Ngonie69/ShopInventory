using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators;

/// <summary>
/// The SAP users who have raised a credit memo approval request the caller can see, in any status —
/// the choices for the queue's "raised by" filter.
/// </summary>
/// <param name="CallerUserId">
/// The caller's account, whose role decides which SAP stages are read — see
/// <see cref="ICreditNoteApprovalStageScope"/>. Null is a service caller and reads every stage.
/// </param>
public sealed record GetCreditNoteApprovalOriginatorsQuery(Guid? CallerUserId = null)
    : IRequest<ErrorOr<List<CreditNoteApprovalOriginatorDto>>>;
