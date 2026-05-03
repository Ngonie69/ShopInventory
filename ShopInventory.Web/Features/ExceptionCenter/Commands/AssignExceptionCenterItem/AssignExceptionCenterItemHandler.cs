using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;

public sealed class AssignExceptionCenterItemHandler(
    IExceptionCenterService exceptionCenterService,
    ILogger<AssignExceptionCenterItemHandler> logger
) : IRequestHandler<AssignExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        AssignExceptionCenterItemCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await exceptionCenterService.AssignItemAsync(request.Source, request.ItemId, cancellationToken);
            return success ? Result.Success : Errors.ExceptionCenter.AssignFailed("Assign request was rejected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error assigning exception center item {Source}:{ItemId}", request.Source, request.ItemId);
            return Errors.ExceptionCenter.AssignFailed("Assign request was rejected.");
        }
    }
}