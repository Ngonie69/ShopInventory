using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Commands.EnsureInvoiceCrateTransaction;

public sealed record EnsureInvoiceCrateTransactionCommand(
    int InvoiceDocNum,
    decimal? ExpectedQuantity,
    Guid? UserId
) : IRequest<ErrorOr<EnsureInvoiceCrateTransactionResponseDto>>;