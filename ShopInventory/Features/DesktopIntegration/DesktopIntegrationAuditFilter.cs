using ShopInventory.Common.Auditing;

namespace ShopInventory.Features.DesktopIntegration;

/// <summary>
/// Audits every desktop request that changes something.
/// </summary>
/// <remarks>
/// <para>
/// The till's reads are stock lookups and queue polls it repeats for as long as it is switched on, so
/// auditing them buries the rows that matter under thousands that do not. The two reads worth a row —
/// the sales list and the end-of-day report, both of which show a shop's takings — log for themselves
/// in their handlers, where the row can say whose money was read.
/// </para>
/// <para>
/// Writes are audited whatever the outcome. A refused sale or a failed post is worth as much as a
/// successful one: it means somebody was trying to move money and could not.
/// </para>
/// </remarks>
public sealed class DesktopIntegrationAuditFilter(
    IServiceScopeFactory scopeFactory,
    ILogger<DesktopIntegrationAuditFilter> logger
) : EndpointAuditFilter(scopeFactory, logger)
{
    protected override string ActionPrefix => "Desktop";

    protected override string EntityType => "DesktopEndpoint";

    protected override string FallbackPath => "/api/desktopintegration";

    protected override bool ShouldAudit(HttpRequest request) => IsMutating(request);
}
