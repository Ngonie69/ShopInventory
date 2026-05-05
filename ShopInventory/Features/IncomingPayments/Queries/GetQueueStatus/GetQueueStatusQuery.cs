using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.IncomingPayments.Queries.GetQueueStatus;

public sealed record GetQueueStatusQuery(
    string ExternalReference
) : IRequest<ErrorOr<IncomingPaymentQueueStatusDto>>;