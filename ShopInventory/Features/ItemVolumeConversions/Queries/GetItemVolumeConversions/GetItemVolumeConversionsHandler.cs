using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;

public sealed class GetItemVolumeConversionsHandler(
    ApplicationDbContext context,
    ILogger<GetItemVolumeConversionsHandler> logger
) : IRequestHandler<GetItemVolumeConversionsQuery, ErrorOr<GetItemVolumeConversionsResult>>
{
    public async Task<ErrorOr<GetItemVolumeConversionsResult>> Handle(
        GetItemVolumeConversionsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = context.ItemVolumeConversions.AsNoTracking();

            if (!request.IncludeInactive)
            {
                query = query.Where(conversion => conversion.IsActive);
            }

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var search = request.Search.Trim();
                query = query.Where(conversion =>
                    EF.Functions.ILike(conversion.ItemCode, $"%{search}%") ||
                    conversion.ItemName != null && EF.Functions.ILike(conversion.ItemName, $"%{search}%"));
            }

            var conversions = await query
                .OrderBy(conversion => conversion.ItemCode)
                .Select(conversion => new ItemVolumeConversionResult
                {
                    ItemCode = conversion.ItemCode,
                    ItemName = conversion.ItemName,
                    VolumeFactor = conversion.VolumeFactor,
                    Notes = conversion.Notes,
                    IsActive = conversion.IsActive,
                    CreatedAt = conversion.CreatedAt,
                    UpdatedAt = conversion.UpdatedAt,
                    UpdatedBy = conversion.UpdatedBy
                })
                .ToListAsync(cancellationToken);

            return new GetItemVolumeConversionsResult
            {
                TotalCount = await context.ItemVolumeConversions.CountAsync(cancellationToken),
                ActiveCount = await context.ItemVolumeConversions
                    .CountAsync(conversion => conversion.IsActive, cancellationToken),
                Conversions = conversions
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading item volume conversions");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }
}
