namespace ShopInventory.DTOs;

/// <summary>
/// The maintenance lockout as the settings screen sees it.
/// </summary>
public class MaintenanceSettingsDto
{
    /// <summary>Whether an operator has the lockout switched on.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the lockout is actually refusing requests right now.
    /// </summary>
    /// <remarks>
    /// Differs from <see cref="Enabled"/> once a window has run out: still enabled, no longer
    /// applying. The screen shows this so nobody reads a spent window as an ongoing lockout.
    /// </remarks>
    public bool IsActive { get; set; }

    /// <summary>"Transactions" or "All".</summary>
    public string Scope { get; set; } = nameof(Features.Maintenance.MaintenanceScope.Transactions);

    /// <summary>What the apps are shown. Blank means the built-in wording.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The wording the apps get when <see cref="Message"/> is blank.</summary>
    public string DefaultMessage { get; set; } = string.Empty;

    /// <summary>
    /// The audiences the lockout applies to: "MobileApps", "WebPortal", "OtherClients".
    /// </summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>Every audience the lockout could name, with the wording the screen shows.</summary>
    public List<MaintenanceAudienceDto> AvailableAudiences { get; set; } = [];

    /// <summary>The app keys covered. Empty means every app.</summary>
    public List<string> AppIds { get; set; } = [];

    /// <summary>The app keys covered, spelled out even when <see cref="AppIds"/> is empty.</summary>
    public List<MaintenanceAppDto> CoveredApps { get; set; } = [];

    /// <summary>Every app the lockout could name, for the screen to offer.</summary>
    public List<MaintenanceAppDto> AvailableApps { get; set; } = [];

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? EndsAtUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>One mobile app, by its catalogue key and the name people call it.</summary>
public class MaintenanceAppDto
{
    public string AppId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>One audience, with the wording the settings screen puts beside its tick box.</summary>
public class MaintenanceAudienceDto
{
    public string Audience { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// What an app gets from the status endpoint, which stays reachable during a lockout so the phone
/// can show a banner and grey out its buttons instead of discovering the lockout by being refused.
/// </summary>
public class MaintenanceStatusDto
{
    /// <summary>Whether this caller is currently locked out.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// The audience this caller was recognised as, so a client can show the right notice — and so
    /// that "why is the portal not frozen" has an answer that does not need the API's logs.
    /// </summary>
    public string Audience { get; set; } = nameof(Features.Maintenance.MaintenanceAudience.OtherClients);

    /// <summary>"Transactions" or "All". Only meaningful while active.</summary>
    public string Scope { get; set; } = nameof(Features.Maintenance.MaintenanceScope.Transactions);

    /// <summary>What to show the user. Blank when nothing is running.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Whether the app may still read. False during an <c>All</c> lockout, so a phone knows not to
    /// bother refreshing its catalogue.
    /// </summary>
    public bool ReadsAllowed { get; set; } = true;

    public DateTime? EndsAtUtc { get; set; }

    public DateTime CheckedAtUtc { get; set; }
}

/// <summary>Set or clear the lockout.</summary>
public class SetMaintenanceRequest
{
    public bool Enabled { get; set; }

    /// <summary>"Transactions" (the default) or "All". Case-insensitive.</summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Who the lockout applies to: "MobileApps", "WebPortal", "OtherClients". Case-insensitive.
    /// Empty or absent means the mobile apps, which is what this switch covered before audiences
    /// existed.
    /// </summary>
    public List<string>? Audiences { get; set; }

    /// <summary>Optional wording for the apps. Blank uses the built-in message.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Optional app keys to narrow the mobile audience to. Empty or absent covers every app, which
    /// is what "stop the phones" normally means, and it has no bearing on the other audiences.
    /// </summary>
    public List<string>? AppIds { get; set; }

    /// <summary>
    /// Optional UTC time at which the lockout lifts by itself. Absent means it stays on until
    /// somebody turns it off.
    /// </summary>
    public DateTime? EndsAtUtc { get; set; }
}

/// <summary>The outcome of setting the lockout.</summary>
public class SetMaintenanceResponse
{
    public string Message { get; set; } = string.Empty;
    public MaintenanceSettingsDto Settings { get; set; } = new();
}
