namespace ShopInventory.Web.Common;

/// <summary>
/// Reduces the API's health report to the one word the dashboard prints.
///
/// This is a separate, testable function because reading the report naively
/// gets it wrong. <c>GET /api/health</c> returns its <em>readiness</em> report
/// as the top-level status, and the SAP check is registered under the
/// <c>dependencies</c> tag only — deliberately, so that a SAP outage does not
/// take the API out of a load balancer's rotation. Read on its own, that
/// top-level status says "Healthy" while SAP is unreachable, which is the one
/// thing an administrator opens the dashboard to find out.
/// </summary>
public static class SystemHealthSummary
{
    private const string Healthy = "Healthy";

    /// <summary>The label and whether it is good news.</summary>
    /// <param name="readinessStatus">The report's top-level status.</param>
    /// <param name="dependenciesStatus">The dependency section's status, if it reported one.</param>
    /// <param name="checkStatuses">Each dependency check's status.</param>
    public static (string Value, bool IsHealthy) Describe(
        string? readinessStatus,
        string? dependenciesStatus,
        IEnumerable<string?> checkStatuses)
    {
        ArgumentNullException.ThrowIfNull(checkStatuses);

        if (string.IsNullOrWhiteSpace(readinessStatus))
        {
            return ("—", true);
        }

        // The API failing its own readiness is the worse of the two, and it
        // already carries the right word.
        if (!IsPassing(readinessStatus))
        {
            return (readinessStatus, false);
        }

        // A missing dependency section is unknown, not broken — say nothing
        // rather than reporting trouble nobody has.
        var sectionFailing = dependenciesStatus is not null && !IsPassing(dependenciesStatus);
        var anyCheckFailing = checkStatuses.Any(status => !IsPassing(status));

        return sectionFailing || anyCheckFailing
            ? ("Degraded", false)
            : (Healthy, true);
    }

    public static bool IsPassing(string? status) =>
        string.Equals(status, Healthy, StringComparison.OrdinalIgnoreCase);
}
