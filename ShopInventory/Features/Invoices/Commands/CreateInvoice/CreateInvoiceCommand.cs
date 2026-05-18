using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.Invoices.Commands.CreateInvoice;

public sealed record CreateInvoiceCommand(
    CreateInvoiceRequest Request,
    bool AutoAllocateBatches,
    BatchAllocationStrategy AllocationStrategy,
    Guid? UserId
) : IRequest<ErrorOr<InvoiceCreatedResponseDto>>;
