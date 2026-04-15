using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.ConvertToInvoice;

public sealed class ConvertToInvoiceHandler(
    ISalesOrderService salesOrderService,
    IAuditService auditService
) : IRequestHandler<ConvertToInvoiceCommand, ErrorOr<InvoiceDto>>
{
    public async Task<ErrorOr<InvoiceDto>> Handle(
        ConvertToInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await salesOrderService.ConvertToInvoiceAsync(command.Id, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.ConvertOrderToInvoice, "SalesOrder", command.Id.ToString(), $"Sales order {command.Id} converted to invoice", true); } catch { }
            return invoice;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.SalesOrder.InvalidOperation(ex.Message);
        }
    }
}
