using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByCustomer;

public sealed record GetPaymentsByCustomerQuery(string CardCode) : IRequest<ErrorOr<object>>;
