using ErrorOr;
using MediatR;

namespace ShopInventory.Features.SalesOrders.Commands.BackfillSalesOrderCardNames;

public sealed record BackfillSalesOrderCardNamesCommand
    : IRequest<ErrorOr<BackfillSalesOrderCardNamesResult>>;