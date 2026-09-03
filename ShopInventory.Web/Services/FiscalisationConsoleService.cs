using System.Globalization;
using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IFiscalisationConsoleService
{
    Task<List<FiscalConsoleDeviceResponse>?> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<FiscalWorkQueueResponse?> GetWorkQueueAsync(
        string? status,
        int? deviceId,
        DateTime? fromDate,
        DateTime? toDate,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<FiscalDayStateListResponse?> GetFiscalDaysAsync(
        int? deviceId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fiscalises a SAP invoice from the work queue and says what actually happened to it.
    /// </summary>
    /// <remarks>
    /// Two calls, because the queue is keyed on the DocNum the platform was asked about and the
    /// fiscalise route is keyed on the SAP DocEntry. The lookup is done here, once, when someone has
    /// clicked — not carried on every row of every page for the small proportion that will ever be
    /// retried.
    /// </remarks>
    Task<FiscalRetryResult> RetryInvoiceAsync(int docNum, CancellationToken cancellationToken = default);
}

/// <summary>
/// The office's read of everything fiscal, and the one action the console offers.
/// </summary>
public class FiscalisationConsoleService(
    HttpClient httpClient,
    IInvoiceService invoiceService,
    ILogger<FiscalisationConsoleService> logger) : IFiscalisationConsoleService
{
    public async Task<List<FiscalConsoleDeviceResponse>?> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<FiscalConsoleDeviceResponse>>(
                "api/fiscalisation-console/devices", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading fiscal devices for the fiscalisation console");
            return null;
        }
    }

    public async Task<FiscalWorkQueueResponse?> GetWorkQueueAsync(
        string? status,
        int? deviceId,
        DateTime? fromDate,
        DateTime? toDate,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            parameters.Add($"status={Uri.EscapeDataString(status)}");
        }

        if (deviceId is > 0)
        {
            parameters.Add($"deviceId={deviceId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (fromDate is not null)
        {
            parameters.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
        }

        if (toDate is not null)
        {
            parameters.Add($"toDate={toDate.Value:yyyy-MM-dd}");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add($"search={Uri.EscapeDataString(search)}");
        }

        try
        {
            return await httpClient.GetFromJsonAsync<FiscalWorkQueueResponse>(
                $"api/fiscalisation-console/work-queue?{string.Join("&", parameters)}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading the fiscalisation work queue");
            return null;
        }
    }

    public async Task<FiscalDayStateListResponse?> GetFiscalDaysAsync(
        int? deviceId,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={page.ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={pageSize.ToString(CultureInfo.InvariantCulture)}"
        };

        if (deviceId is > 0)
        {
            parameters.Add($"deviceId={deviceId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            parameters.Add($"status={Uri.EscapeDataString(status)}");
        }

        try
        {
            return await httpClient.GetFromJsonAsync<FiscalDayStateListResponse>(
                $"api/fiscalisation-console/fiscal-days?{string.Join("&", parameters)}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading fiscal days for the fiscalisation console");
            return null;
        }
    }

    public async Task<FiscalRetryResult> RetryInvoiceAsync(int docNum, CancellationToken cancellationToken = default)
    {
        InvoiceDto? invoice;

        try
        {
            invoice = await invoiceService.GetInvoiceByDocNumAsync(docNum);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not look up invoice #{DocNum} before fiscalising it", docNum);

            return FiscalRetryResult.Blocked(
                $"Invoice #{docNum} could not be looked up in SAP, so nothing was submitted.");
        }

        if (invoice is null || invoice.DocEntry <= 0)
        {
            return FiscalRetryResult.Blocked(
                $"SAP has no invoice #{docNum}. The queue entry is stale — reload the page.");
        }

        try
        {
            var result = await invoiceService.FiscalizeInvoiceAsync(invoice.DocEntry);

            return FiscalRetryResult.From(docNum, result);
        }
        catch (Exception ex)
        {
            // Everything from here is unknown, and unknown is not the same as "nothing happened".
            // FiscalizeInvoiceAsync throws on every non-2xx and on every transport fault, so a read
            // timeout on a submission that did reach FDMS arrives here looking exactly like a request
            // that never left. Reporting that as blocked-and-retryable is how one sale gets two fiscal
            // receipts, and a fiscal receipt cannot be withdrawn.
            logger.LogError(ex, "Fiscalising invoice #{DocNum} from the console gave no usable answer", docNum);

            return FiscalRetryResult.Unknown(docNum, ApiErrorResponse.GetFriendlyMessage(ex, fallbackMessage:
                "The server gave no answer that says whether the invoice reached FDMS."));
        }
    }
}

/// <summary>
/// What a retry actually did, once the platform's error code has been read.
/// </summary>
public enum FiscalRetryOutcome
{
    /// <summary>Fiscalised now. A receipt exists that did not before.</summary>
    Fiscalised,

    /// <summary>
    /// A receipt already existed. A success, not a failure — the document is compliant, and the only
    /// thing that was wrong was this application's record of it.
    /// </summary>
    AlreadyFiscalised,

    /// <summary>
    /// The outcome is unknown and must be resolved by looking the receipt up. Nothing may be sent again.
    /// </summary>
    Reconcile,

    /// <summary>Refused for a stated reason, with nothing recorded at FDMS. Safe to try again later.</summary>
    Failed,

    /// <summary>Nothing was submitted, and nothing was going to be — the platform is off or in dry run.</summary>
    Skipped,

    /// <summary>
    /// The request was made and no usable answer came back, so whether FDMS holds a receipt is not
    /// known.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Blocked"/>, which is the honest "nothing was sent", and from
    /// <see cref="Reconcile"/>, where the platform itself said it could not tell. This one is our own
    /// ignorance — a dropped connection, a read timeout, a 502 from something in between — and it has
    /// the same consequence: the document must be looked up, never sent again.
    /// </remarks>
    Unknown,

    /// <summary>The submission never left this application.</summary>
    Blocked
}

/// <summary>
/// A retry's outcome, classified from the platform's error code rather than from whether the call
/// returned a 200.
/// </summary>
/// <remarks>
/// This classification is the reason the console exists rather than a Fiscalise button on a list.
/// <c>AlreadyFiscalised</c> arrives as a failure and is a success; an indeterminate outcome arrives as
/// a failure and must never be retried, because the receipt may exist at FDMS and a duplicate fiscal
/// receipt cannot be withdrawn. Treating all three as "it went red, press it again" is how one sale
/// ends up fiscalised twice.
/// </remarks>
public sealed class FiscalRetryResult
{
    private static readonly string[] ReconcileErrorCodes =
    [
        "FdmsOperationIndeterminate",
        "RequiresReconciliation",
        "ChainBreak"
    ];

    /// <summary>
    /// The prefix the platform uses for every conflict on its (taxpayer, receipt type, invoice number)
    /// key. Matched as a family rather than by name: a new one means a new way for two documents to
    /// collide on one receipt, and defaulting an unrecognised idempotency conflict to "retry" is the
    /// wrong way round.
    /// </summary>
    private const string IdempotencyErrorCodePrefix = "IDEMPOTENCY_";

    private FiscalRetryResult(FiscalRetryOutcome outcome, string message, string? errorCode, string? detail)
    {
        Outcome = outcome;
        Message = message;
        ErrorCode = errorCode;
        Detail = detail;
    }

    public FiscalRetryOutcome Outcome { get; }

    public string Message { get; }

    public string? ErrorCode { get; }

    /// <summary>The platform's own wording, kept verbatim for the row's error line.</summary>
    public string? Detail { get; }

    /// <summary>Whether the document is now compliant, however it got there.</summary>
    public bool IsSettled =>
        Outcome is FiscalRetryOutcome.Fiscalised or FiscalRetryOutcome.AlreadyFiscalised;

    /// <summary>Whether trying again could make things worse rather than better.</summary>
    /// <remarks>
    /// <see cref="FiscalRetryOutcome.Unknown"/> counts, and that is the whole point of it existing. An
    /// exception carries no disposition, so the only safe reading of one is that the submission may have
    /// reached FDMS — and the console must not invite a second one on a maybe.
    /// </remarks>
    public bool MustNotRetry =>
        Outcome is FiscalRetryOutcome.Reconcile or FiscalRetryOutcome.Unknown;

    public static FiscalRetryResult Blocked(string message) =>
        new(FiscalRetryOutcome.Blocked, message, null, null);

    /// <summary>
    /// The submission was attempted and the outcome could not be established.
    /// </summary>
    /// <param name="docNum">
    /// The invoice, named in the sentence the operator is asked to act on. It has to be the number
    /// they would search the fiscalisation platform by, because looking it up there is the only way
    /// out of an Unknown.
    /// </param>
    /// <param name="detail">
    /// What the API actually said, kept verbatim. A ProblemDetails body usually names the real fault —
    /// "the fiscalisation platform did not respond within 30 seconds" — and replacing that with a
    /// generic sentence throws away the only thing that tells the operator where to look.
    /// </param>
    public static FiscalRetryResult Unknown(int docNum, string? detail) =>
        new(
            FiscalRetryOutcome.Unknown,
            $"It is not known whether invoice #{docNum} reached FDMS. Do not send it again — look the "
                + "receipt up on the fiscalisation platform first.",
            null,
            detail);

    public static FiscalRetryResult From(int docNum, FiscalizationResult result)
    {
        var code = result.ErrorCode;
        var detail = result.ErrorDetails ?? result.Message;

        // First, and ahead of Success, because the platform reports it as a 400. Left as a failure it
        // raises a fresh incident every time a completed invoice is looked at again.
        if (IsAlreadyFiscalised(code) || (result.Success && result.Skipped))
        {
            return new FiscalRetryResult(
                FiscalRetryOutcome.AlreadyFiscalised,
                result.Message ?? $"Invoice #{docNum} was already fiscalised. Nothing was submitted.",
                code,
                detail);
        }

        if (result.RequiresReconciliation || IsReconcileCode(code))
        {
            return new FiscalRetryResult(
                FiscalRetryOutcome.Reconcile,
                $"Invoice #{docNum} is unresolved and must not be sent again. Look the receipt up on the "
                    + "fiscalisation console — it may already exist.",
                code,
                detail);
        }

        if (result.Success)
        {
            return new FiscalRetryResult(
                FiscalRetryOutcome.Fiscalised,
                result.Message ?? $"Invoice #{docNum} is fiscalised.",
                code,
                detail);
        }

        if (result.Skipped)
        {
            return new FiscalRetryResult(
                FiscalRetryOutcome.Skipped,
                result.Message ?? $"Nothing was submitted for invoice #{docNum}.",
                code,
                detail);
        }

        return new FiscalRetryResult(
            FiscalRetryOutcome.Failed,
            result.Message ?? $"Invoice #{docNum} was refused.",
            code,
            detail);
    }

    private static bool IsAlreadyFiscalised(string? errorCode) =>
        string.Equals(errorCode, "AlreadyFiscalised", StringComparison.OrdinalIgnoreCase);

    private static bool IsReconcileCode(string? errorCode) =>
        !string.IsNullOrWhiteSpace(errorCode)
        && (errorCode.StartsWith(IdempotencyErrorCodePrefix, StringComparison.OrdinalIgnoreCase)
            || ReconcileErrorCodes.Any(known =>
                errorCode.Contains(known, StringComparison.OrdinalIgnoreCase)));
}

// Mirrors of the API's DTOs. Nullability has to match the API side exactly — a non-nullable property
// here against a null on the wire throws in System.Text.Json and the page reports no data at all.

public class FiscalConsoleDeviceResponse
{
    public int DeviceId { get; set; }

    /// <summary>False means every ZIMRA-side field below is unset, and <see cref="PlatformError"/> says why.</summary>
    public bool Reachable { get; set; }

    public string? PlatformError { get; set; }

    public string? SerialNumber { get; set; }

    public string? BranchName { get; set; }

    /// <summary><c>Online</c> or <c>Offline</c>, as ZIMRA registered the device.</summary>
    public string? OperatingMode { get; set; }

    public DateTime? CertificateValidTill { get; set; }

    public int? CertificateDaysRemaining { get; set; }

    public int? TaxPayerDayMaxHrs { get; set; }

    public int? FiscalDayNo { get; set; }

    public string? FiscalDayStatus { get; set; }

    public DateTime? FiscalDayOpened { get; set; }

    public double? FiscalDayHoursElapsed { get; set; }

    public int? FiscalDayPercentOfMax { get; set; }

    public DateTime? LastReceiptDate { get; set; }

    public int? LastReceiptGlobalNo { get; set; }

    public int? LastReceiptCounter { get; set; }

    public string? OfflineSigningHolder { get; set; }

    /// <summary>Null means the handset has not reported, which is not the same as none.</summary>
    public int? OfflineSigningHolderPendingSales { get; set; }

    public DateTime? OfflineSigningHolderLastSeenAtUtc { get; set; }

    public int AwaitingHandover { get; set; }

    public int FailedHandover { get; set; }

    public int Unsignable { get; set; }

    public int Unstamped { get; set; }

    public bool ChainBroken { get; set; }

    public int? ChainBrokenAtReceiptGlobalNo { get; set; }

    public string? ChainBrokenError { get; set; }

    public int BlockedBehindChainBreak { get; set; }
}

public class FiscalWorkQueueResponse
{
    public List<FiscalConsoleWorkItemResponse> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int SaleCount { get; set; }

    public int DocumentCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    public bool HasMore { get; set; }
}

public class FiscalConsoleWorkItemResponse
{
    public string Key { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public int? SaleId { get; set; }

    public int? DocNum { get; set; }

    public string Reference { get; set; } = string.Empty;

    public string? CustomerName { get; set; }

    public string? WarehouseCode { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    /// <summary><c>Fiscalisation</c> or <c>Hand-over</c> — which step is still owed.</summary>
    public string Stage { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>A Nocturne family name: <c>neutral</c>, <c>warn</c> or <c>bad</c>.</summary>
    public string Severity { get; set; } = string.Empty;

    public int? DeviceId { get; set; }

    public int? ReceiptGlobalNo { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>One of <see cref="FiscalWorkQueueDisposition"/>. Decides whether a retry is offered at all.</summary>
    public string Disposition { get; set; } = string.Empty;

    public string DispositionNote { get; set; } = string.Empty;
}

/// <summary>The API's disposition values, restated so the page compares against a constant.</summary>
public static class FiscalWorkQueueDisposition
{
    public const string Retry = "retry";
    public const string Reconcile = "reconcile";
    public const string Unrecoverable = "unrecoverable";
    public const string Automatic = "automatic";

    /// <summary>No scheduled run owns this row, and this page cannot send it either.</summary>
    public const string Stalled = "stalled";
}

/// <summary>The API's work queue filter keys, restated for the filter control.</summary>
public static class FiscalWorkQueueFilter
{
    public const string All = "all";
    public const string AwaitingFiscalisation = "awaiting-fiscalisation";
    public const string FiscalisationFailed = "fiscalisation-failed";
    public const string NeedsReconciliation = "needs-reconciliation";
    public const string HandoverFailed = "handover-failed";
    public const string ChainBroken = "chain-broken";
    public const string Unsignable = "unsignable";
    public const string Unstamped = "unstamped";

    /// <summary>
    /// The severity family of the rows a filter selects, mirroring
    /// <c>FiscalWorkQueueFilters.SeverityOf</c> on the API.
    /// </summary>
    /// <remarks>
    /// One function rather than a family literal per menu row, so a filter's swatch cannot end up a
    /// different colour from the dot on the rows it selects — the swatch is the only thing that says
    /// what choosing it will show.
    /// </remarks>
    public static string SeverityOf(string filter) => filter switch
    {
        NeedsReconciliation or ChainBroken or Unsignable => "bad",
        FiscalisationFailed or HandoverFailed or Unstamped => "warn",
        _ => "neutral"
    };
}

/// <summary>The fiscal day lifecycle filter keys, restated for the filter control.</summary>
public static class FiscalDayStatusFilter
{
    /// <summary>The two statuses a person has to act on, as one filter.</summary>
    public const string NeedsAttention = "needs-attention";

    /// <summary>The lifecycle in the order it happens, which is the order the menu reads in.</summary>
    public static readonly string[] Lifecycle =
    [
        "Open",
        "Drained",
        "Closed",
        "FileGenerated",
        "Submitted",
        "NeedsReconciliation",
        "Failed"
    ];
}

public class FiscalDayStateListResponse
{
    public List<FiscalDayStateResponse> Days { get; set; } = [];

    public int TotalCount { get; set; }

    public int OutstandingCount { get; set; }

    public int NeedsAttentionCount { get; set; }

    public int Page { get; set; }

    public int PageSize { get; set; }

    public bool HasMore { get; set; }
}

public class FiscalDayStateResponse
{
    public int Id { get; set; }

    public int DeviceId { get; set; }

    public int FiscalDayNo { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>The taxpayer's wall clock, with no offset. Rendered as it arrives.</summary>
    public DateTime? OpenedAtLocal { get; set; }

    public int? MaxDurationHours { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public DateTime? FileGeneratedAtUtc { get; set; }

    public DateTime? FileSubmittedAtUtc { get; set; }

    public string? OfflineFileReference { get; set; }

    public int IngestedReceiptCount { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public bool DurationWarningRaised { get; set; }

    public bool IsComplete { get; set; }

    public bool NeedsAttention { get; set; }

    public DateTime UpdatedAt { get; set; }
}
