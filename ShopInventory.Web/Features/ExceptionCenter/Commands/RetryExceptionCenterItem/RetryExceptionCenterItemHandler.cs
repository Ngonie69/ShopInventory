using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;

public sealed class RetryExceptionCenterItemHandler(
    IExceptionCenterService exceptionCenterService,
    ILogger<RetryExceptionCenterItemHandler> logger
) : IRequestHandler<RetryExceptionCenterItemCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RetryExceptionCenterItemCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await exceptionCenterService.RetryItemAsync(request.Source, request.ItemKey, cancellationToken);
            return success ? Result.Success : Errors.ExceptionCenter.RetryFailed("Retry request was rejected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrying exception center item {Source}:{ItemKey}", request.Source, request.ItemKey);
            return Errors.ExceptionCenter.RetryFailed("Retry request was rejected.");
        }
    }
}