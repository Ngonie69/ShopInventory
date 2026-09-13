using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Shops;

/// <summary>
/// The rules for <see cref="ShopEntity.SupplyingWarehouseCode"/>: how it is stored, and how a till's
/// stock request finds its source warehouse from it.
/// </summary>
public static class ShopSupplyingWarehouse
{
    /// <summary>Trimmed, with blank stored as absent — the same treatment as the cost centre.</summary>
    public static string? Normalise(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    /// <summary>
    /// A shop supplied from its own warehouse. Refused: the request would move stock from the shop to
    /// itself, which is the mistake the till's old hardcoded picker made easiest to make.
    /// </summary>
    public static bool IsOwnWarehouse(string? supplyingWarehouseCode, string warehouseCode) =>
        supplyingWarehouseCode is not null &&
        string.Equals(supplyingWarehouseCode, warehouseCode.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The warehouse a stock request is raised against.
    /// </summary>
    /// <remarks>
    /// For an account on a shop, the shop decides and what the till sent is ignored. The till only
    /// ever sends what it was handed at sign-in, so the two differ only when an administrator has
    /// changed the shop since — and then the shop is the answer. A shop with none set is refused with
    /// a message saying where to set it, rather than falling back to whatever the till had.
    ///
    /// An account with no shop — anything other than a till operator — keeps sending its own, as it
    /// always has.
    /// </remarks>
    public static ErrorOr<string> ResolveForRequest(ShopEntity? shop, string? requestedFromWarehouse)
    {
        if (shop is not null)
        {
            return Normalise(shop.SupplyingWarehouseCode) is { } assigned
                ? assigned
                : Errors.Shops.NoSupplyingWarehouse(shop.Name);
        }

        return Normalise(requestedFromWarehouse) is { } requested
            ? requested
            : Errors.DesktopIntegration.SourceWarehouseRequired;
    }
}
