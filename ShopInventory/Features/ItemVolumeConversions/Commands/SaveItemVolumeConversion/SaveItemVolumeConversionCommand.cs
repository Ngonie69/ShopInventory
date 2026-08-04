using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;

/// <summary>
/// Creates the factor for an item that has none, or replaces the one it has.
/// </summary>
public sealed record SaveItemVolumeConversionCommand(
    string ItemCode,
    string? ItemName,
    decimal VolumeFactor,
    string? Notes,
    bool IsActive,
    string? UpdatedBy
) : IRequest<ErrorOr<ItemVolumeConversionResult>>;
