using ShopInventory.Common.Auditing;

namespace ShopInventory.Features.VanSalesReports;

/// <summary>
/// Audits every read on the van sales portal surface, <c>/api/van-sales</c>.
/// </summary>
/// <remarks>
/// <para>
/// The mirror image of the desktop filter. There the writes were unaudited and the reads were a till
/// polling; here every write already logs from its handler, and the reads are people — a supervisor
/// opening the performance report, a manager pulling one rep's compliance for a month. What sold, by
/// whom and where is the most sensitive thing this surface shows, and nothing recorded who looked.
/// </para>
/// <para>
/// Reads only, so the writes are not recorded twice. Nothing on the web app polls these routes, so a
/// row is a page being opened rather than a timer firing.
/// </para>
/// <para>
/// The query string is kept, because it is the substance of the row: <c>userId</c>, <c>routeCode</c>,
/// <c>fromDate</c> and <c>toDate</c> are what say whose figures were read and for which period. None
/// of these routes takes anything secret in it.
/// </para>
/// </remarks>
public sealed class VanSalesPortalReadAuditFilter(
    IServiceScopeFactory scopeFactory,
    ILogger<VanSalesPortalReadAuditFilter> logger
) : EndpointAuditFilter(scopeFactory, logger)
{
    protected override string ActionPrefix => "VanSalesPortal";

    protected override string EntityType => "VanSalesPortalEndpoint";

    protected override string FallbackPath => "/api/van-sales";

    protected override bool RecordQueryString => true;

    protected override bool ShouldAudit(HttpRequest request) => !IsMutating(request);
}
