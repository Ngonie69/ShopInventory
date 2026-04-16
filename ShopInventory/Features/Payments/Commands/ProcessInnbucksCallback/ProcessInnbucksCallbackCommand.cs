using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Payments.Commands.ProcessInnbucksCallback;

public sealed record ProcessInnbucksCallbackCommand(
    PaymentCallbackPayload Payload,
    string? Signature
) : IRequest<ErrorOr<Success>>;
