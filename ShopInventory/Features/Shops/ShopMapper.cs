using System.Linq.Expressions;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Shops;

/// <summary>
/// The one definition of a shop's wire shape.
/// </summary>
/// <remarks>
/// An expression rather than a method taking an entity, so that the list query, the single-shop
/// query and the three commands all project through the same code and EF translates it to SQL rather
/// than loading rows to shape them in memory. A second hand-written mapping would be a second place
/// for <see cref="ShopDto.AssignedOperatorCount"/> to be counted differently.
/// </remarks>
public static class ShopMapper
{
    /// <summary>
    /// Counts only active operators: a disabled account cannot sell, so it is not something closing
    /// the shop would strand, and including it would block a close for a person who has left.
    /// </summary>
    public static readonly Expression<Func<ShopEntity, ShopDto>> Projection =
        shop => new ShopDto(
            shop.Id,
            shop.Code,
            shop.Name,
            shop.BusinessPartnerCode,
            shop.WarehouseCode,
            shop.CostCentreCode,
            shop.IsActive,
            shop.Users.Count(user => user.IsActive),
            shop.CreatedAt,
            shop.UpdatedAt);
}
