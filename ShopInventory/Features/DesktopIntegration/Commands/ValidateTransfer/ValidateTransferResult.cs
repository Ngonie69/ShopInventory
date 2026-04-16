using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.ValidateTransfer;

public sealed record ValidateTransferResult(
    bool IsValid,
    string Message,
    List<ValidateTransferErrorItem> Errors,
    int LinesValidated
);

public sealed record ValidateTransferErrorItem(
    string? ItemCode,
    string? WarehouseCode,
    string? Message
);
