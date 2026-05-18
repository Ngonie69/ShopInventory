using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Commands.CreateCrateGrv;

public sealed record CreateCrateGrvCommand(
    int CrateTransactionId,
    string Reason,
    Stream FileStream,
    string FileName,
    string ContentType,
    Guid? UserId
) : IRequest<ErrorOr<CrateGrvDto>>;