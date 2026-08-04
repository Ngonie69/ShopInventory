using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;

public sealed class SaveItemVolumeConversionHandler(
    HttpClient httpClient,
    ILogger<SaveItemVolumeConversionHandler> logger
) : IRequestHandler<SaveItemVolumeConversionCommand, ErrorOr<ItemVolumeConversionResult>>
{
    public async Task<ErrorOr<ItemVolumeConversionResult>> Handle(
        SaveItemVolumeConversionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var itemCode = command.ItemCode.Trim().ToUpperInvariant();
            var url = $"api/itemvolumeconversion/{Uri.EscapeDataString(itemCode)}";

            var response = await httpClient.PutAsJsonAsync(
                url,
                new
                {
                    itemName = command.ItemName,
                    volumeFactor = command.VolumeFactor,
                    notes = command.Notes,
                    isActive = command.IsActive,
                    updatedBy = command.UpdatedBy
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Failed to save volume conversion for {ItemCode}. Status code: {StatusCode}. Body: {Body}",
                    itemCode,
                    (int)response.StatusCode,
                    body);

                return Errors.ItemVolumeConversion.SaveFailed($"Failed to save the volume conversion factor for {itemCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<ItemVolumeConversionResult>(
                cancellationToken: cancellationToken);

            if (result is null)
            {
                return Errors.ItemVolumeConversion.SaveFailed($"Failed to save the volume conversion factor for {itemCode}.");
            }

            logger.LogInformation(
                "Saved volume conversion factor {Factor} for item {ItemCode}",
                result.VolumeFactor,
                result.ItemCode);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving volume conversion for item {ItemCode}", command.ItemCode);
            return Errors.ItemVolumeConversion.SaveFailed("Failed to save the volume conversion factor.");
        }
    }
}
