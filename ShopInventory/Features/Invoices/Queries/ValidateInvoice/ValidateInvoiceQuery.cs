using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.Invoices.Queries.ValidateInvoice;

public sealed record ValidateInvoiceQuery(
    CreateInvoiceRequest Request,
    bool AutoAllocateBatches,
    BatchAllocationStrategy AllocationStrategy
) : IRequest<ErrorOr<object>>;
