using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Commands.UpdateCrateOpeningBalance;

public sealed record UpdateCrateOpeningBalanceCommand(
    int CrateTransactionId,
    string ShopCardCode,
    decimal Quantity,
    DateTime EffectiveDate,
    string? Notes,
    Stream? FileStream,
    string? FileName,
    string? ContentType,
    Guid? UserId
) : IRequest<ErrorOr<CrateTransactionDto>>;