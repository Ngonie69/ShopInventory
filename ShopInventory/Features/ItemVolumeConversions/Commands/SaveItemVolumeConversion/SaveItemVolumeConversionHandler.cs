using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;

public sealed class SaveItemVolumeConversionHandler(
    ApplicationDbContext context,
    ILogger<SaveItemVolumeConversionHandler> logger
) : IRequestHandler<SaveItemVolumeConversionCommand, ErrorOr<ItemVolumeConversionResult>>
{
    public async Task<ErrorOr<ItemVolumeConversionResult>> Handle(
        SaveItemVolumeConversionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            // Item codes are stored upper-cased so the report's lookups can stay ordinal; entering
            // "yog100" must land on the same row as "YOG100" rather than create a second one.
            var itemCode = command.ItemCode.Trim().ToUpperInvariant();

            var conversion = await context.ItemVolumeConversions
                .FirstOrDefaultAsync(existing => existing.ItemCode == itemCode, cancellationToken);

            var isNew = conversion is null;
            if (conversion is null)
            {
                conversion = new ItemVolumeConversionEntity
                {
                    ItemCode = itemCode,
                    CreatedAt = DateTime.UtcNow
                };

                context.ItemVolumeConversions.Add(conversion);
            }

            conversion.ItemName = string.IsNullOrWhiteSpace(command.ItemName) ? null : command.ItemName.Trim();
            conversion.VolumeFactor = command.VolumeFactor;
            conversion.Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim();
            conversion.IsActive = command.IsActive;
            conversion.UpdatedAt = DateTime.UtcNow;
            conversion.UpdatedBy = string.IsNullOrWhiteSpace(command.UpdatedBy) ? null : command.UpdatedBy.Trim();

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "{Action} volume conversion factor {Factor} for item {ItemCode} by {UpdatedBy}",
                isNew ? "Created" : "Updated",
                conversion.VolumeFactor,
                conversion.ItemCode,
                conversion.UpdatedBy ?? "unknown");

            return new ItemVolumeConversionResult
            {
                ItemCode = conversion.ItemCode,
                ItemName = conversion.ItemName,
                VolumeFactor = conversion.VolumeFactor,
                Notes = conversion.Notes,
                IsActive = conversion.IsActive,
                CreatedAt = conversion.CreatedAt,
                UpdatedAt = conversion.UpdatedAt,
                UpdatedBy = conversion.UpdatedBy
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving volume conversion factor for item {ItemCode}", command.ItemCode);
            return Errors.ItemVolumeConversion.SaveFailed(ex.Message);
        }
    }
}
