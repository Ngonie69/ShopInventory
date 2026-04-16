using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.CreateQuotation;

public sealed class CreateQuotationHandler(
    IQuotationService quotationService,
    ILogger<CreateQuotationHandler> logger
) : IRequestHandler<CreateQuotationCommand, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        CreateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var quotation = await quotationService.CreateAsync(command.Request, command.UserId, cancellationToken);
            return quotation;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating quotation");
            var message = ex.InnerException?.Message ?? ex.Message;
            return Errors.Quotation.CreationFailed(message);
        }
    }
}
