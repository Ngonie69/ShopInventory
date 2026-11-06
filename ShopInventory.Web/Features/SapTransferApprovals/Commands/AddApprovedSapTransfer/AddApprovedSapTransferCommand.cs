using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;

public sealed record AddApprovedSapTransferCommand(int DraftEntry, string ClientRequestId)
    : IRequest<ErrorOr<AddApprovedSapTransferResultDto>>;
