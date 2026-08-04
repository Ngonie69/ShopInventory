using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Commands.DeleteItemVolumeConversion;

public sealed class DeleteItemVolumeConversionHandler(
    HttpClient httpClient,
    ILogger<DeleteItemVolumeConversionHandler> logger
) : IRequestHandler<DeleteItemVolumeConversionCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteItemVolumeConversionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var itemCode = command.ItemCode.Trim().ToUpperInvariant();
            var response = await httpClient.DeleteAsync(
                $"api/itemvolumeconversion/{Uri.EscapeDataString(itemCode)}",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Failed to delete volume conversion for {ItemCode}. Status code: {StatusCode}. Body: {Body}",
                    itemCode,
                    (int)response.StatusCode,
                    body);

                return Errors.ItemVolumeConversion.DeleteFailed($"Failed to remove the volume conversion factor for {itemCode}.");
            }

            logger.LogInformation("Deleted volume conversion factor for item {ItemCode}", itemCode);
            return Result.Deleted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting volume conversion for item {ItemCode}", command.ItemCode);
            return Errors.ItemVolumeConversion.DeleteFailed("Failed to remove the volume conversion factor.");
        }
    }
}
