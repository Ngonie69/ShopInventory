using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.DeleteQuotation;

public sealed class DeleteQuotationHandler(
    IQuotationService quotationService,
    ILogger<DeleteQuotationHandler> logger
) : IRequestHandler<DeleteQuotationCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await quotationService.DeleteAsync(command.Id, cancellationToken);
            if (!deleted)
                return Errors.Quotation.NotFound(command.Id);

            return Result.Deleted;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Quotation.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting quotation {Id}", command.Id);
            return Errors.Quotation.DeleteFailed(ex.Message);
        }
    }
}
