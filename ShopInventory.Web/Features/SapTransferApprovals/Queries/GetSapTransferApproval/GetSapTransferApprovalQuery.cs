using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapTransferApprovals.Queries.GetSapTransferApproval;

public sealed record GetSapTransferApprovalQuery(int DraftEntry) : IRequest<ErrorOr<SapTransferApprovalDetailDto>>;
