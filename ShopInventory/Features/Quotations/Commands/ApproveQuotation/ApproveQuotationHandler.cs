using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.ApproveQuotation;

public sealed class ApproveQuotationHandler(
    IQuotationService quotationService,
    ILogger<ApproveQuotationHandler> logger
) : IRequestHandler<ApproveQuotationCommand, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        ApproveQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var quotation = await quotationService.ApproveAsync(command.Id, command.UserId, cancellationToken);
            return quotation;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Quotation.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error approving quotation {Id}", command.Id);
            return Errors.Quotation.ApprovalFailed(ex.Message);
        }
    }
}
