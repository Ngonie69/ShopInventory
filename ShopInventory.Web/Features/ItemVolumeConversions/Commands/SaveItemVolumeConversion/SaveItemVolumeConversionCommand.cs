using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;

public sealed record SaveItemVolumeConversionCommand(
    string ItemCode,
    string? ItemName,
    decimal VolumeFactor,
    string? Notes,
    bool IsActive,
    string? UpdatedBy
) : IRequest<ErrorOr<ItemVolumeConversionResult>>;
