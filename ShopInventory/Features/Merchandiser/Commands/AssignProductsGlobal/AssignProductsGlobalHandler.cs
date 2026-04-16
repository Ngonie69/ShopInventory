using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser.Queries.GetGlobalProducts;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Commands.AssignProductsGlobal;

public sealed class AssignProductsGlobalHandler(
    ApplicationDbContext context,
    ISender sender,
    ILogger<AssignProductsGlobalHandler> logger
) : IRequestHandler<AssignProductsGlobalCommand, ErrorOr<MerchandiserProductListResponseDto>>
{
    public async Task<ErrorOr<MerchandiserProductListResponseDto>> Handle(
        AssignProductsGlobalCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (request.ItemCodes == null || request.ItemCodes.Count == 0)
            return Errors.Merchandiser.AssignmentFailed("At least one item code is required");

        var merchandiserIds = await context.Users
            .AsNoTracking()
            .Where(u => u.Role == "Merchandiser" && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var productNames = request.ItemNames ?? new Dictionary<string, string>();
        if (productNames.Count == 0)
        {
            try
            {
                productNames = await context.Products
                    .AsNoTracking()
                    .Where(p => request.ItemCodes.Contains(p.ItemCode))
                    .ToDictionaryAsync(p => p.ItemCode, p => p.ItemName ?? "", cancellationToken);
            }
            catch { /* proceed without names */ }
        }

        var globalStatus = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => request.ItemCodes.Contains(mp.ItemCode) && mp.UpdatedAt != null)
            .GroupBy(mp => mp.ItemCode)
            .Select(g => new { ItemCode = g.Key, IsActive = g.OrderByDescending(mp => mp.UpdatedAt).First().IsActive })
            .ToDictionaryAsync(x => x.ItemCode, x => x.IsActive, cancellationToken);

        int totalAdded = 0;

        foreach (var merchandiserId in merchandiserIds)
        {
            var existing = await context.MerchandiserProducts
                .Where(mp => mp.MerchandiserUserId == merchandiserId)
                .Select(mp => mp.ItemCode)
                .ToListAsync(cancellationToken);

            var newCodes = request.ItemCodes.Except(existing).ToList();

            var newEntities = newCodes.Select(code => new MerchandiserProductEntity
            {
                MerchandiserUserId = merchandiserId,
                ItemCode = code,
                ItemName = productNames.GetValueOrDefault(code),
                IsActive = globalStatus.GetValueOrDefault(code, true),
                CreatedAt = DateTime.UtcNow,
                UpdatedBy = command.Username
            }).ToList();

            context.MerchandiserProducts.AddRange(newEntities);
            totalAdded += newEntities.Count;
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Assigned {Count} products to {MerchCount} merchandisers",
            request.ItemCodes.Count, merchandiserIds.Count);

        return await sender.Send(new GetGlobalProductsQuery(), cancellationToken);
    }
}
