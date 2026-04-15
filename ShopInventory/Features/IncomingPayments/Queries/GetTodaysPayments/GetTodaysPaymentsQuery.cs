using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetTodaysPayments;

public sealed record GetTodaysPaymentsQuery() : IRequest<ErrorOr<IncomingPaymentDateResponseDto>>;
