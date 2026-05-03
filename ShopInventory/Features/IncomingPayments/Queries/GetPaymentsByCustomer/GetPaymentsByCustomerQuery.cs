using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByCustomer;

public sealed record GetPaymentsByCustomerQuery(
    string CardCode,
    DateTime? FromDate = null,
    DateTime? ToDate = null) : IRequest<ErrorOr<IncomingPaymentDateResponseDto>>;
