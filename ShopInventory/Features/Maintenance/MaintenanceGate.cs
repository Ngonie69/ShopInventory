namespace ShopInventory.Features.Maintenance;

/// <summary>
/// The decision the lockout comes down to: may this one request through?
/// </summary>
/// <remarks>
/// Pure, static and free of <c>HttpContext</c> so that the rule can be tested as a rule. The
/// middleware is then only plumbing — read the headers, ask this, write the 503 — and the cases
/// that matter (a phone during a lockout, a phone reading during a lockout, the portal during a
/// lockout aimed only at phones, an admin during a lockout aimed at the portal, anyone once the
/// window has passed) are unit tests rather than a deployment.
/// </remarks>
public static class MaintenanceGate
{
    /// <summary>
    /// What a phone may still reach while the lockout is on, whatever its scope.
    /// </summary>
    /// <remarks>
    /// Every one of these is something an app needs in order to behave well *during* a lockout.
    /// Signing in stays open so a driver opening the app sees the maintenance notice rather than a
    /// login failure they will read as their password being wrong; the version and status checks
    /// stay open so the app can find out that it is a lockout and when it lifts; push registration
    /// stays open so the phone can still be told when it is over; health stays open because it is
    /// not the app's to be refused.
    ///
    /// /api/maintenance covers the whole controller rather than only its status endpoint, which is
    /// what makes the switch reachable to turn off. A lockout that froze the portal and then froze
    /// the screen that lifts it would have to be cleared with a SQL statement against the database
    /// somebody was in the middle of restoring.
    /// </remarks>
    private static readonly string[] AlwaysAllowedPrefixes =
    [
        "/api/auth",
        "/api/twofactor",
        "/api/password",
        "/api/van-sales-customer/auth",
        "/api/vansales/auth",
        "/api/appversion",
        "/api/maintenance",
        "/api/pushnotification/register",
        "/api/pushnotification/unregister",
        "/api/health",
        "/health",
        "/hubs/notifications",
        "/api/hubs/notifications",
        "/swagger"
    ];

    /// <summary>
    /// Reads that this API happens to expose as POSTs, because their filter is a body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this list the transaction lockout would take away the van app's order and invoice
    /// history, which are searches with a date range in the body and change nothing — and taking
    /// reads away is the thing the <see cref="MaintenanceScope.Transactions"/> scope exists
    /// to avoid. Verb is otherwise a good proxy for "changes something"; these are the exceptions,
    /// and they are an allowlist so that a new POST counts as a transaction until somebody decides
    /// otherwise.
    /// </para>
    /// <para>
    /// <c>MaintenanceRouteClassificationTests</c> sweeps the controllers and fails if an
    /// entry here stops being a POST route or stops dispatching a query, so the list cannot quietly
    /// rot into something that waves writes through.
    /// </para>
    /// </remarks>
    public static readonly string[] ReadOnlyPostRoutes =
    [
        "/api/vansales/sales-order/history",
        "/api/vansales/order/history",
        "/api/stock/warehouse/{warehouseCode}/sales",
        "/api/crates/pods/validate-bulk",
        "/api/invoice/pods/validate-bulk"
    ];

    /// <summary>How long a phone is told to wait when no end time was set.</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether this one request gets through.
    /// </summary>
    /// <param name="state">The lockout as an operator set it.</param>
    /// <param name="nowUtc">Now, so a window that has run out stops applying.</param>
    /// <param name="caller">Which audience the request belongs to, and which app if it is a phone.</param>
    /// <param name="callerIsExemptAdmin">
    /// Whether an Admin is behind this request. Only ever true where the caller has been
    /// authenticated, and only ever consulted for the web portal — see
    /// <see cref="MaintenanceAdminExemption"/>.
    /// </param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path.</param>
    public static MaintenanceDecision Evaluate(
        MaintenanceState state,
        DateTime nowUtc,
        MaintenanceCaller caller,
        bool callerIsExemptAdmin,
        string method,
        string path)
    {
        if (!state.IsActiveAt(nowUtc))
        {
            return MaintenanceDecision.Allowed;
        }

        if (!state.CoversAudience(caller.Audience))
        {
            return MaintenanceDecision.Allowed;
        }

        // Naming apps narrows the phones and nothing else. A lockout on van sales and the web
        // portal must still take the whole portal, which has no app id to be narrowed by.
        if (caller.Audience == MaintenanceAudience.MobileApps && !state.CoversApp(caller.PolicyKey))
        {
            return MaintenanceDecision.Allowed;
        }

        var normalizedPath = Normalize(path);
        if (IsAlwaysAllowed(normalizedPath))
        {
            return MaintenanceDecision.Allowed;
        }

        // Asked here rather than in the middleware so that "which audiences have an exemption" is a
        // rule with a test rather than a property of where the code happens to run. A phone held by
        // an admin is still a phone in a van, and the lockout is about the van.
        if (callerIsExemptAdmin && caller.Audience == MaintenanceAudience.WebPortal)
        {
            return MaintenanceDecision.Allowed;
        }

        if (state.Scope == MaintenanceScope.Transactions && IsRead(method, normalizedPath))
        {
            return MaintenanceDecision.Allowed;
        }

        return MaintenanceDecision.Blocked(state, RetryAfter(state, nowUtc));
    }

    /// <summary>
    /// Whether the request only reads.
    /// </summary>
    public static bool IsRead(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return true;
        }

        if (!HttpMethods.IsPost(method))
        {
            return false;
        }

        var normalizedPath = Normalize(path);
        return ReadOnlyPostRoutes.Any(route => Matches(route, normalizedPath));
    }

    /// <summary>Whether the path stays reachable no matter what the lockout says.</summary>
    public static bool IsAlwaysAllowed(string path)
    {
        var normalizedPath = Normalize(path);
        return AlwaysAllowedPrefixes.Any(prefix =>
            normalizedPath.Equals(prefix, StringComparison.Ordinal)
            || normalizedPath.StartsWith(prefix + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// How long to tell the phone to wait, so it backs off instead of retrying in a loop.
    /// </summary>
    /// <remarks>
    /// An end time in the future gives the real answer. Without one, a flat five minutes: long
    /// enough that a van full of handsets is not hammering an API that is mid-maintenance, short
    /// enough that trading resumes promptly once the switch goes back.
    /// </remarks>
    private static TimeSpan RetryAfter(MaintenanceState state, DateTime nowUtc)
    {
        if (state.EndsAtUtc is not { } endsAt || endsAt <= nowUtc)
        {
            return DefaultRetryAfter;
        }

        var remaining = endsAt - nowUtc;
        return remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
    }

    /// <summary>Lower-cased, query-stripped, with no trailing slash, so comparisons are literal.</summary>
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var trimmed = path.Trim();
        var queryStart = trimmed.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            trimmed = trimmed[..queryStart];
        }

        trimmed = trimmed.ToLowerInvariant();
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    /// <summary>
    /// Segment-wise match, where a <c>{placeholder}</c> in the route stands for exactly one segment.
    /// </summary>
    private static bool Matches(string routeTemplate, string path)
    {
        var routeSegments = Normalize(routeTemplate).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (routeSegments.Length != pathSegments.Length)
        {
            return false;
        }

        for (var i = 0; i < routeSegments.Length; i++)
        {
            var routeSegment = routeSegments[i];
            if (routeSegment.StartsWith('{') && routeSegment.EndsWith('}'))
            {
                continue;
            }

            if (!routeSegment.Equals(pathSegments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
