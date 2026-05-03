using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;

public sealed class AcknowledgeExceptionCenterItemHandler(
    IExceptionCenterService exceptionCenterService,
    ILogger<AcknowledgeExceptionCenterItemHandler> logger
) : IRequestHandler<AcknowledgeExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AcknowledgeExceptionCenterItemCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await exceptionCenterService.AcknowledgeItemAsync(request.Source, request.ItemId, cancellationToken);
            return success ? Result.Success : Errors.ExceptionCenter.AcknowledgeFailed("Acknowledge request was rejected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acknowledging exception center item {Source}:{ItemId}", request.Source, request.ItemId);
            return Errors.ExceptionCenter.AcknowledgeFailed("Acknowledge request was rejected.");
        }
    }
}