using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.ValidateInvoice;

public sealed record ValidateInvoiceQuery(
    CreateDesktopInvoiceRequest Request,
    bool AutoAllocateBatches = true,
    BatchAllocationStrategy AllocationStrategy = BatchAllocationStrategy.FEFO
) : IRequest<ErrorOr<ValidateInvoiceResult>>;

public sealed record ValidateInvoiceResult(
    bool IsValid,
    string Message,
    string Strategy,
    int LinesValidated,
    int BatchesAllocated,
    object? AllocatedLines,
    List<string>? Warnings
);
