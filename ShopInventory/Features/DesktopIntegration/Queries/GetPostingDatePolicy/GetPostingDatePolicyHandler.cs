using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Services;
using static ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy.PostingDatePolicyKeys;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;

public sealed class GetPostingDatePolicyHandler(ApplicationDbContext db)
    : IRequestHandler<GetPostingDatePolicyQuery, ErrorOr<PostingDatePolicy>>
{
    public async Task<ErrorOr<PostingDatePolicy>> Handle(
        GetPostingDatePolicyQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == AllowCustomPostingDate || config.Key == ChangedBy)
            .Select(config => new { config.Key, config.Value, config.UpdatedAt })
            .ToListAsync(cancellationToken);

        var flag = rows.FirstOrDefault(row => row.Key == AllowCustomPostingDate);

        return new PostingDatePolicy(
            bool.TryParse(flag?.Value, out var on) && on,
            AuditService.ToCAT(DateTime.UtcNow).Date,
            flag?.UpdatedAt,
            rows.FirstOrDefault(row => row.Key == ChangedBy)?.Value);
    }
}
