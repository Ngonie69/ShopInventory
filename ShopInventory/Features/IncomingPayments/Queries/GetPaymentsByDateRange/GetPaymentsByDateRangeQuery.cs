using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByDateRange;

public sealed record GetPaymentsByDateRangeQuery(DateTime FromDate, DateTime ToDate) : IRequest<ErrorOr<IncomingPaymentDateResponseDto>>;
