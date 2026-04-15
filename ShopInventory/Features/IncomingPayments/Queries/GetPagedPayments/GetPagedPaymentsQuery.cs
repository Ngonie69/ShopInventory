using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPagedPayments;

public sealed record GetPagedPaymentsQuery(int Page, int PageSize) : IRequest<ErrorOr<IncomingPaymentListResponseDto>>;
