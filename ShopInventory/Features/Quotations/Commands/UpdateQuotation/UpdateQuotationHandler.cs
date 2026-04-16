using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.UpdateQuotation;

public sealed class UpdateQuotationHandler(
    IQuotationService quotationService,
    ILogger<UpdateQuotationHandler> logger
) : IRequestHandler<UpdateQuotationCommand, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        UpdateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var quotation = await quotationService.UpdateAsync(command.Id, command.Request, cancellationToken);
            return quotation;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Quotation.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating quotation {Id}", command.Id);
            return Errors.Quotation.UpdateFailed(ex.Message);
        }
    }
}
