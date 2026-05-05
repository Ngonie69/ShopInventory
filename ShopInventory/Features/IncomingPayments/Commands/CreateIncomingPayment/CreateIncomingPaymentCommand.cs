using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.IncomingPayments.Commands.CreateIncomingPayment;

public sealed record CreateIncomingPaymentCommand(
    CreateIncomingPaymentRequest Request,
    string? CreatedBy = null
) : IRequest<ErrorOr<IncomingPaymentCreatedResponseDto>>;
