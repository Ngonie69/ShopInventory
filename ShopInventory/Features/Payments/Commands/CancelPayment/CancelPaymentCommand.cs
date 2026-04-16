using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Payments.Commands.CancelPayment;

public sealed record CancelPaymentCommand(
    int Id,
    string? Username
) : IRequest<ErrorOr<Success>>;
