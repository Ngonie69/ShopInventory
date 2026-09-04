using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Shops.Commands.CreateShop;

public sealed class CreateShopHandler(
    ApplicationDbContext context,
    ILogger<CreateShopHandler> logger
) : IRequestHandler<CreateShopCommand, ErrorOr<ShopDto>>
{
    public async Task<ErrorOr<ShopDto>> Handle(
        CreateShopCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var code = request.Code.Trim();
        var warehouseCode = request.WarehouseCode.Trim();

        var codeTaken = await context.Shops
            .AsNoTracking()
            .AnyAsync(shop => shop.Code.ToUpper() == code.ToUpper(), cancellationToken);

        if (codeTaken)
        {
            return Errors.Shops.DuplicateCode(code);
        }

        // Checked across closed shops too, not just trading ones. A closed shop still owns its sales
        // history, and that history is scoped by warehouse — so handing its warehouse to a new shop
        // would show the old shop's takings to the new shop's operators.
        var warehouseOwner = await context.Shops
            .AsNoTracking()
            .FirstOrDefaultAsync(
                shop => shop.WarehouseCode.ToUpper() == warehouseCode.ToUpper(),
                cancellationToken);

        if (warehouseOwner is not null)
        {
            return Errors.Shops.WarehouseAlreadyAssigned(warehouseCode, warehouseOwner.Name);
        }

        var entity = new ShopEntity
        {
            Code = code,
            Name = request.Name.Trim(),
            BusinessPartnerCode = request.BusinessPartnerCode.Trim(),
            WarehouseCode = warehouseCode,
            CostCentreCode = string.IsNullOrWhiteSpace(request.CostCentreCode)
                ? null
                : request.CostCentreCode.Trim(),
            IsActive = true,
            CreatedByUserId = command.UserId,
            CreatedAt = DateTime.UtcNow
        };

        context.Shops.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Shop {ShopCode} ({ShopName}) opened on warehouse {WarehouseCode} and business partner {CardCode} by {UserId}",
            entity.Code, entity.Name, entity.WarehouseCode, entity.BusinessPartnerCode, command.UserId);

        return await context.Shops
            .AsNoTracking()
            .Where(shop => shop.Id == entity.Id)
            .Select(ShopMapper.Projection)
            .FirstAsync(cancellationToken);
    }
}
