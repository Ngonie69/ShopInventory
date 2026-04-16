using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Payments.Commands.InitiatePayment;

public sealed record InitiatePaymentCommand(
    InitiatePaymentRequest Request,
    string? Username
) : IRequest<ErrorOr<InitiatePaymentResponse>>;
