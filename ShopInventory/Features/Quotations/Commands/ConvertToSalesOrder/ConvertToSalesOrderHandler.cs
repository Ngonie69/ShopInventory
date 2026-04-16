using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.ConvertToSalesOrder;

public sealed class ConvertToSalesOrderHandler(
    IQuotationService quotationService,
    ILogger<ConvertToSalesOrderHandler> logger
) : IRequestHandler<ConvertToSalesOrderCommand, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        ConvertToSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var salesOrder = await quotationService.ConvertToSalesOrderAsync(command.Id, command.UserId, cancellationToken);
            if (salesOrder == null)
                return Errors.Quotation.ConversionFailed($"Quotation {command.Id} could not be converted to a sales order");

            return salesOrder;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Quotation.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting quotation {Id} to sales order", command.Id);
            return Errors.Quotation.ConversionFailed(ex.Message);
        }
    }
}
