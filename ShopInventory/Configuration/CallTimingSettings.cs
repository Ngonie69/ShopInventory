namespace ShopInventory.Configuration;

/// <summary>
/// How much of the API's own traffic <see cref="ShopInventory.Services.CallTimings.CallTimingRecorder"/>
/// keeps in memory for the Admin call timings page.
/// </summary>
/// <remarks>
/// Each kept call is one fixed-size record in a ring, so the capacities bound the memory: about 48 bytes a
/// call, so the defaults hold roughly 5 MB of SAP calls and 7 MB of inbound calls. When a ring is full the
/// oldest call is dropped, and the page says from when its figures run, so a busy day reads as "since
/// 11:40" rather than as a quiet one. Nothing is written to the database and a recycle starts both rings
/// empty.
/// </remarks>
public sealed class CallTimingSettings
{
    public const string SectionName = "CallTimings";

    /// <summary>False stops recording altogether; the page then says timings are off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many SAP Service Layer calls are kept, newest first.</summary>
    public int SapCallCapacity { get; set; } = 100_000;

    /// <summary>How many inbound API requests are kept, newest first.</summary>
    public int InboundCallCapacity { get; set; } = 150_000;
}
