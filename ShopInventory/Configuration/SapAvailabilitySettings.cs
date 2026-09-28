namespace ShopInventory.Configuration;

/// <summary>
/// How the API decides, for the whole cluster, that SAP is down and that it is back.
/// </summary>
/// <remarks>
/// The defaults declare an outage after about a minute and a half of SAP not answering, and end it
/// after about a minute of it answering again. Slower than the per-node circuit breaker on purpose:
/// the breaker protects a node from hammering SAP for thirty seconds, while an outage changes what the
/// posting passes do and is recorded for good, so it should not be declared on a blip.
/// </remarks>
public sealed class SapAvailabilitySettings
{
    public const string SectionName = "SapAvailability";

    /// <summary>Whether the probe runs at all. Off, no outage is ever declared.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often SAP is probed.</summary>
    public int ProbeIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How long a probe waits for SAP. Well under the interval, so a hung probe cannot make the next
    /// one late, and far under a posting request's own timeout: the probe asks for one warehouse code.
    /// </summary>
    public int ProbeTimeoutSeconds { get; set; } = 20;

    /// <summary>Failed probes in a row that declare an outage.</summary>
    public int FailuresToDeclare { get; set; } = 3;

    /// <summary>Successful probes in a row that end one.</summary>
    public int SuccessesToEnd { get; set; } = 2;

    /// <summary>
    /// The furthest back an outage can stretch a posting pass's lookback, in days. A bound, so an
    /// outage row left open by accident cannot make every pass read the whole sales history.
    /// </summary>
    public int MaxLookbackExtensionDays { get; set; } = 30;
}
