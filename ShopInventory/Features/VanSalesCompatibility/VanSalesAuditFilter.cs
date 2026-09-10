using ShopInventory.Common.Auditing;

namespace ShopInventory.Features.VanSalesCompatibility;

/// <summary>
/// Audits every handset request, reads included.
/// </summary>
/// <remarks>
/// The handset is the only record of a van's day that the office can read, and a rep's reads are part
/// of that record — which customers were pulled up, whose history was looked at — so the read side is
/// kept rather than filtered out the way the desktop surface filters it.
/// </remarks>
public sealed class VanSalesAuditFilter(
    IServiceScopeFactory scopeFactory,
    ILogger<VanSalesAuditFilter> logger
) : EndpointAuditFilter(scopeFactory, logger)
{
    protected override string ActionPrefix => "VanSales";

    protected override string EntityType => "VanSalesEndpoint";

    protected override string FallbackPath => "/api/vansales";
}
