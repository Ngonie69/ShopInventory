using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.TransferListener.Queries.GetTransferListenerStatus;

public sealed record GetTransferListenerStatusQuery(int RecentDocumentCount = 20)
    : IRequest<ErrorOr<TransferListenerStatusModel>>;
