using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.UpdateQuotationStatus;

public sealed class UpdateQuotationStatusHandler(
    IQuotationService quotationService,
    ILogger<UpdateQuotationStatusHandler> logger
) : IRequestHandler<UpdateQuotationStatusCommand, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        UpdateQuotationStatusCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var quotation = await quotationService.UpdateStatusAsync(
                command.Id, command.Status, command.UserId, command.Comments, cancellationToken);
            return quotation;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Quotation.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating quotation status {Id}", command.Id);
            return Errors.Quotation.UpdateFailed(ex.Message);
        }
    }
}
