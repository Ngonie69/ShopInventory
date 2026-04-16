using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Payments.Queries.CheckPaymentStatus;

public sealed record CheckPaymentStatusQuery(int Id) : IRequest<ErrorOr<PaymentStatusResponse>>;
