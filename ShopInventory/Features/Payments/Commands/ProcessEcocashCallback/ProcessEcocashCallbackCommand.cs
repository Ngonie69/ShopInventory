using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Payments.Commands.ProcessEcocashCallback;

public sealed record ProcessEcocashCallbackCommand(
    PaymentCallbackPayload Payload,
    string? Signature
) : IRequest<ErrorOr<Success>>;
