namespace ShopInventory.Web.Models;

// Hand-written mirrors of the API's GetCallTimingsResult (ShopInventory/Features/CallTimings/Queries/
// GetCallTimings). Nullability matches the API's field for field: a value type the API sends as null
// and this side declares plain makes ReadFromJsonAsync throw, and the page would read "could not load".
// Only FirstKeptSlotFreesAtUtc is nullable there. Every time is UTC, every duration milliseconds.

/// <summary>What <c>GET api/call-timings</c> answers.</summary>
public sealed class CallTimingsModel
{
    public bool Enabled { get; set; }
    public string Window { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime WindowStartUtc { get; set; }
    public DateTime RecordingSinceUtc { get; set; }
    public DateTime SapCoverageStartUtc { get; set; }
    public DateTime InboundCoverageStartUtc { get; set; }
    public int SapCalls { get; set; }
    public int InboundCalls { get; set; }
    public SapSlotUsageModel Slots { get; set; } = new();
    public SapSlotWaitSummaryModel SlotWaits { get; set; } = new();
    public List<SapFamilyTimingModel> Families { get; set; } = [];
    public List<SapCallTimingModel> Slowest { get; set; } = [];
    public List<SapCallInFlightTimingModel> InFlight { get; set; } = [];
    public List<InboundRouteTimingModel> Routes { get; set; } = [];
    public List<InboundCallerTimingModel> Callers { get; set; } = [];
}

public sealed class SapSlotUsageModel
{
    public int Limit { get; set; }
    public int InUse { get; set; }
    public int BackgroundLimit { get; set; }
    public int BackgroundInUse { get; set; }
    public List<SapReadFamilySlotsModel> Families { get; set; } = [];
}

public sealed class SapReadFamilySlotsModel
{
    public string Family { get; set; } = string.Empty;
    public int Limit { get; set; }
    public int InUse { get; set; }
    public int Kept { get; set; }
    public DateTime? FirstKeptSlotFreesAtUtc { get; set; }
}

public sealed class SapSlotWaitSummaryModel
{
    public int Calls { get; set; }
    public int Waited { get; set; }
    public int WaitedOverTwoSeconds { get; set; }
    public int P95Ms { get; set; }
    public int MaxMs { get; set; }
    public int MaxFamilyWaitMs { get; set; }
    public int MaxSlotWaitMs { get; set; }
    public double TotalSeconds { get; set; }
    public List<SapPriorityWaitModel> ByPriority { get; set; } = [];
}

public sealed class SapPriorityWaitModel
{
    public string Priority { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int Waited { get; set; }
    public int P95Ms { get; set; }
    public int MaxMs { get; set; }
}

public sealed class SapFamilyTimingModel
{
    public string Family { get; set; } = string.Empty;
    public bool Limited { get; set; }
    public int Calls { get; set; }
    public int Interactive { get; set; }
    public int Ok { get; set; }
    public int Errors { get; set; }
    public int Timeouts { get; set; }
    public int GaveUp { get; set; }
    public int NotSent { get; set; }
    public int PresumedStuck { get; set; }
    public int P50Ms { get; set; }
    public int P95Ms { get; set; }
    public int MaxMs { get; set; }
    public double TotalSeconds { get; set; }
    public int Waited { get; set; }
    public int WaitP95Ms { get; set; }
    public int MaxWaitMs { get; set; }
    public int InFlightNow { get; set; }
}

public sealed class SapCallTimingModel
{
    public DateTime StartedUtc { get; set; }
    public string Family { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public int AtSapMs { get; set; }
    public int FamilyWaitMs { get; set; }
    public int SlotWaitMs { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public bool Interactive { get; set; }
    public bool PresumedStuck { get; set; }
    public string Origin { get; set; } = string.Empty;
}

public sealed class SapCallInFlightTimingModel
{
    public DateTime StartedUtc { get; set; }
    public int AgeMs { get; set; }
    public string Family { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public int AtSapMs { get; set; }
    public bool Interactive { get; set; }
    public string Origin { get; set; } = string.Empty;
}

public sealed class InboundRouteTimingModel
{
    public string Method { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int ServerErrors { get; set; }
    public int ClientErrors { get; set; }
    public int P50Ms { get; set; }
    public int P95Ms { get; set; }
    public int MaxMs { get; set; }
    public double TotalSeconds { get; set; }
    public int SapCalls { get; set; }
    public double SapSeconds { get; set; }
    public List<InboundCallerShareModel> TopCallers { get; set; } = [];
}

public sealed class InboundCallerShareModel
{
    public string Caller { get; set; } = string.Empty;
    public int Calls { get; set; }
}

public sealed class InboundCallerTimingModel
{
    public string Caller { get; set; } = string.Empty;
    public int Calls { get; set; }
    public int ServerErrors { get; set; }
    public int P95Ms { get; set; }
    public double TotalSeconds { get; set; }
}
