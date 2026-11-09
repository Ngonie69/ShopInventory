using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalOriginators;

/// <summary>The SAP users who have raised a credit memo approval request — the "raised by" choices.</summary>
public sealed record GetCreditNoteApprovalOriginatorsQuery : IRequest<ErrorOr<List<CreditNoteApprovalOriginatorDto>>>;
