namespace ShopInventory.Features.Maintenance;

/// <summary>
/// Who a maintenance lockout is aimed at.
/// </summary>
/// <remarks>
/// These partition the traffic: every request the API serves belongs to exactly one of them, so
/// ticking all three is a genuine "nothing gets through" rather than a hopeful one. That is the
/// property worth keeping if a fourth is ever added — an audience that leaves a gap is worse than
/// no audience at all, because the screen would say the system is frozen while something kept
/// trading.
/// </remarks>
public enum MaintenanceAudience
{
    /// <summary>
    /// The Android handsets: van sales, POD, customer orders.
    /// </summary>
    /// <remarks>
    /// The original reason this feature exists, and the one audience that can be narrowed further
    /// to particular apps. Recognised from the headers the apps send — see
    /// <c>MobileClientRequest</c>.
    /// </remarks>
    MobileApps = 0,

    /// <summary>
    /// The office web portal.
    /// </summary>
    /// <remarks>
    /// Recognised from the <c>X-Client-App</c> header the Web puts on every call to the API, which
    /// is also how the Web's own background cache sweeps are counted — they are the portal too, and
    /// a sweep that rebuilds a cache from a database being restored is exactly what a lockout is
    /// for. Admins are exempt from this audience; see <see cref="MaintenanceAdminExemption"/>.
    /// </remarks>
    WebPortal = 1,

    /// <summary>
    /// Everything else that reaches the API: the KefShop tills, the transfer event listener, the
    /// fiscalisation tool, the customer portal, scripts.
    /// </summary>
    /// <remarks>
    /// Deliberately a catch-all rather than a list. The till is in another repository and sends
    /// nothing that identifies it, so an audience that tried to name the callers would have to
    /// guess, and every caller it failed to recognise would keep trading through a lockout that
    /// claimed to have stopped it. A catch-all is blunt — turning it on stops the transfer listener
    /// and the fiscal feeds along with the tills — which is why it is off by default and the screen
    /// says what it takes down.
    /// </remarks>
    OtherClients = 2
}

/// <summary>
/// The audiences as the screen, the stored settings and the validator deal with them.
/// </summary>
public static class MaintenanceAudiences
{
    /// <summary>
    /// What the lockout covers when nothing has said otherwise.
    /// </summary>
    /// <remarks>
    /// The phones, because that is what the switch did before audiences existed, and a deployment
    /// that read its stored settings and quietly widened them to the whole company would be a nasty
    /// surprise for whoever had it switched on at the time.
    /// </remarks>
    public static readonly IReadOnlyList<MaintenanceAudience> Default = [MaintenanceAudience.MobileApps];

    public static readonly IReadOnlyList<MaintenanceAudience> All =
        [.. Enum.GetValues<MaintenanceAudience>()];

    private static readonly Dictionary<MaintenanceAudience, string> DisplayNames = new()
    {
        [MaintenanceAudience.MobileApps] = "Mobile apps",
        [MaintenanceAudience.WebPortal] = "Web portal",
        [MaintenanceAudience.OtherClients] = "Tills and integrations"
    };

    private static readonly Dictionary<MaintenanceAudience, string> Descriptions = new()
    {
        [MaintenanceAudience.MobileApps] =
            "The Android handsets — van sales, POD and customer orders. Narrow this to particular apps below.",
        [MaintenanceAudience.WebPortal] =
            "The office web portal, including its background cache syncs. Admins keep working.",
        [MaintenanceAudience.OtherClients] =
            "The KefShop tills, the transfer listener, the fiscalisation tool and anything else calling the API."
    };

    public static string GetDisplayName(MaintenanceAudience audience) =>
        DisplayNames.TryGetValue(audience, out var name) ? name : audience.ToString();

    public static string GetDescription(MaintenanceAudience audience) =>
        Descriptions.TryGetValue(audience, out var text) ? text : string.Empty;

    /// <summary>
    /// The audience this name refers to, however it was spelled.
    /// </summary>
    public static bool TryParse(string? value, out MaintenanceAudience audience)
    {
        audience = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out audience) && Enum.IsDefined(audience);
    }

    public static bool IsSupported(string? value) => TryParse(value, out _);

    /// <summary>
    /// Whether this audience can only be judged once the caller has been identified.
    /// </summary>
    /// <remarks>
    /// Only the web portal, and only because admins are exempt from it — a question that cannot be
    /// answered from headers. It decides which of the two places the lockout runs: everything else
    /// is refused before authentication, which keeps a refused request from touching the database
    /// that is the very thing under maintenance. See <c>MaintenanceMiddleware</c>.
    /// </remarks>
    public static bool IsJudgedAfterAuthorization(MaintenanceAudience audience) =>
        audience == MaintenanceAudience.WebPortal;
}
