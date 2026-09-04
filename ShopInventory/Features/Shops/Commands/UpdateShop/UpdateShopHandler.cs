using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Commands.UpdateShop;

public sealed class UpdateShopHandler(
    ApplicationDbContext context,
    ILogger<UpdateShopHandler> logger
) : IRequestHandler<UpdateShopCommand, ErrorOr<ShopDto>>
{
    public async Task<ErrorOr<ShopDto>> Handle(
        UpdateShopCommand command,
        CancellationToken cancellationToken)
    {
        var shop = await context.Shops
            .FirstOrDefaultAsync(candidate => candidate.Id == command.ShopId, cancellationToken);

        if (shop is null)
        {
            return Errors.Shops.NotFound(command.ShopId);
        }

        var request = command.Request;
        var warehouseCode = request.WarehouseCode.Trim();

        var warehouseOwner = await context.Shops
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id != shop.Id &&
                             candidate.WarehouseCode.ToUpper() == warehouseCode.ToUpper(),
                cancellationToken);

        if (warehouseOwner is not null)
        {
            return Errors.Shops.WarehouseAlreadyAssigned(warehouseCode, warehouseOwner.Name);
        }

        // Logged before the change so the previous values survive in the audit trail. Moving a shop's
        // warehouse or business partner redirects every till at that counter on the operators' next
        // sale, without any of them doing anything, so it is worth being able to reconstruct.
        if (!string.Equals(shop.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(shop.BusinessPartnerCode, request.BusinessPartnerCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Shop {ShopCode} selling identity changed by {UserId}: business partner {OldCardCode} -> {NewCardCode}, warehouse {OldWarehouse} -> {NewWarehouse}",
                shop.Code, command.UserId,
                shop.BusinessPartnerCode, request.BusinessPartnerCode.Trim(),
                shop.WarehouseCode, warehouseCode);
        }

        shop.Name = request.Name.Trim();
        shop.BusinessPartnerCode = request.BusinessPartnerCode.Trim();
        shop.WarehouseCode = warehouseCode;
        shop.CostCentreCode = string.IsNullOrWhiteSpace(request.CostCentreCode)
            ? null
            : request.CostCentreCode.Trim();
        shop.UpdatedByUserId = command.UserId;
        shop.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        return await context.Shops
            .AsNoTracking()
            .Where(candidate => candidate.Id == shop.Id)
            .Select(ShopMapper.Projection)
            .FirstAsync(cancellationToken);
    }
}
