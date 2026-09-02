using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;

public sealed record GetCreditNoteApprovalQuery(int Code) : IRequest<ErrorOr<CreditNoteApprovalDetailDto>>;
