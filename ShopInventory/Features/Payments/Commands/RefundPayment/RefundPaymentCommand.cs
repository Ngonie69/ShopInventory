using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Payments.Commands.RefundPayment;

public sealed record RefundPaymentCommand(
    int Id,
    decimal? Amount,
    string? Username
) : IRequest<ErrorOr<Success>>;
