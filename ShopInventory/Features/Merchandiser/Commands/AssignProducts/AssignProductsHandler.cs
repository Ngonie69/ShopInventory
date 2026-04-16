using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser.Queries.GetMerchandiserProducts;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Commands.AssignProducts;

public sealed class AssignProductsHandler(
    ApplicationDbContext context,
    ISender sender,
    ILogger<AssignProductsHandler> logger
) : IRequestHandler<AssignProductsCommand, ErrorOr<MerchandiserProductListResponseDto>>
{
    public async Task<ErrorOr<MerchandiserProductListResponseDto>> Handle(
        AssignProductsCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .FirstOrDefaultAsync(u => u.Id == command.UserId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
            return Errors.Merchandiser.NotFound(command.UserId);

        if (command.Request.ItemCodes == null || command.Request.ItemCodes.Count == 0)
            return Errors.Merchandiser.AssignmentFailed("At least one item code is required");

        var existing = await context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == command.UserId)
            .Select(mp => mp.ItemCode)
            .ToListAsync(cancellationToken);

        var newCodes = command.Request.ItemCodes.Except(existing).ToList();

        var productNames = await context.Products
            .AsNoTracking()
            .Where(p => newCodes.Contains(p.ItemCode))
            .ToDictionaryAsync(p => p.ItemCode, p => p.ItemName, cancellationToken);

        var newEntities = newCodes.Select(code => new MerchandiserProductEntity
        {
            MerchandiserUserId = command.UserId,
            ItemCode = code,
            ItemName = productNames.GetValueOrDefault(code),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedBy = command.Username
        }).ToList();

        context.MerchandiserProducts.AddRange(newEntities);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Assigned {Count} products to merchandiser {UserId}", newCodes.Count, command.UserId);

        return await sender.Send(new GetMerchandiserProductsQuery(command.UserId), cancellationToken);
    }
}
