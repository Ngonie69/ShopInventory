using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Commands.DecideFactoryTransfer;

public sealed record DecideFactoryTransferCommand(
    int TransferId,
    string Decision,
    string? Reason,
    IReadOnlyList<DecideFactoryTransferLineDto> Lines,
    Guid UserId) : IRequest<ErrorOr<FactoryTransferActionResultDto>>
{
    public const string Accept = "Accept";
    public const string Reject = "Reject";

    public bool IsAccept => string.Equals(Decision?.Trim(), Accept, StringComparison.OrdinalIgnoreCase);
}
