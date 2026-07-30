using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrderByClientRequestId;

public sealed record GetMobileOrderByClientRequestIdQuery(
    Guid UserId,
    string ClientRequestId
) : IRequest<ErrorOr<SalesOrderDto>>;
