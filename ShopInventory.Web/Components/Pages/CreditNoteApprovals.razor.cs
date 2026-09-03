using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;
using ShopInventory.Web.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The queue of A/R credit memos SAP is holding for approval, and the two things a manager does with
/// one: decide it, and add the approved one as the credit note.
/// </summary>
/// <remarks>
/// Everything on this page is read live from SAP through the API — there is no local mirror — so the
/// customer filter is applied to the page in hand rather than sent to SAP, and every action reloads
/// both the row and the list from SAP rather than patching what is on screen.
/// </remarks>
public partial class CreditNoteApprovals : IAsyncDisposable
{
    private const int PageSize = 25;

    /// <summary>How long the typing rests before the rows are narrowed to it.</summary>
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(200);

    private static readonly (string Value, string Label)[] StatusOptions =
    [
        ("open", "Awaiting or approved"),
        ("pending", "Awaiting approval"),
        ("approved", "Approved, not added"),
        ("all", "All")
    ];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<CreditNoteApprovals> Logger { get; set; } = default!;

    private List<CreditNoteApprovalListItemDto> requests = [];

    /// <summary>Cleared whenever <see cref="requests"/> or the applied filter changes.</summary>
    private List<CreditNoteApprovalListItemDto>? visibleRequests;

    private int totalCount;
    private int currentPage = 1;

    /// <summary>
    /// The cursor each page was read with, one per page reached, <c>[0]</c> being null for the top.
    /// Going back pops rather than recounting, because the count is what moves.
    /// </summary>
    /// <remarks>
    /// The queue is newest-first and live. Paging by offset re-serves a row the manager has already
    /// decided every time a credit memo is raised behind them, and buries one they never see; the
    /// cursor names where page N stopped, so page N+1 carries on from exactly there no matter how
    /// much has arrived above it. Anything that changes which queue is being read — the status
    /// filter, a refresh, an action — starts the walk again from the top.
    /// </remarks>
    private readonly List<int?> pageCursors = [null];

    /// <summary>Where the next page starts; null when SAP has no more below this one.</summary>
    private int? nextCursor;
    private bool isLoading = true;
    private string? errorMessage;

    private string statusFilter = "open";

    /// <summary>What is in the box. The table filters on <see cref="appliedCustomerFilter"/> instead.</summary>
    private string? customerFilter;

    /// <summary>
    /// The filter the rows are actually narrowed by, which lags the typing by
    /// <see cref="FilterDebounce"/>.
    /// </summary>
    /// <remarks>
    /// Every keystroke is a round trip on the circuit either way, but it used to re-diff all 25 rows
    /// with it — every cell rebuilt and compared for a letter that usually changes nothing — which is
    /// what made typing here feel heavy. Now the box keeps up with the person and the table redraws
    /// once they pause.
    /// </remarks>
    private string? appliedCustomerFilter;

    private int customerFilterVersion;

    private int? detailCode;
    private CreditNoteApprovalDetailDto? detail;
    private bool isLoadingDetail;
    private string? detailError;
    private string? decisionRemarks;

    private bool isSubmitting;
    private string? submittingAction;
    private bool showAddConfirm;

    private bool showViewer;
    private bool isLoadingViewer;
    private string? attachmentError;
    private CreditNoteDraftAttachmentDto? viewerFile;
    private string? viewerFileName;
    private string? viewerMimeType;
    private string? viewerObjectUrl;

    private int TotalPages => Math.Max(1, (int)Math.Ceiling(totalCount / (double)PageSize));

    /// <summary>
    /// SAP's answer decides, not the arithmetic: a short page means there is nothing below it, and
    /// the total is counted separately so it can disagree with what the page actually held.
    /// </summary>
    private bool CanGoNext => nextCursor is not null && currentPage < TotalPages;

    /// <summary>
    /// The customer filter narrows the page in hand; SAP already answered for this page. Held rather
    /// than recomputed because the markup reads it twice per render — once for the rows and once to
    /// decide whether to show the empty note — and it used to walk and re-allocate the page both times.
    /// </summary>
    private List<CreditNoteApprovalListItemDto> VisibleRequests => visibleRequests ??=
        string.IsNullOrWhiteSpace(appliedCustomerFilter)
            ? requests
            : requests.Where(request =>
                (request.CardCode?.Contains(appliedCustomerFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (request.CardName?.Contains(appliedCustomerFilter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

    private string RangeLabel
    {
        get
        {
            if (totalCount == 0)
            {
                return "No requests";
            }

            var first = ((currentPage - 1) * PageSize) + 1;
            var last = Math.Min(currentPage * PageSize, totalCount);
            return $"{first}–{last} of {totalCount:N0}";
        }
    }

    private string EmptyMessage => string.IsNullOrWhiteSpace(appliedCustomerFilter)
        ? statusFilter switch
        {
            "pending" => "No credit memos are awaiting approval in SAP",
            "approved" => "No approved credit memos are waiting to be added",
            "all" => "SAP holds no credit memo approval requests",
            _ => "Nothing is waiting on a decision or an add"
        }
        : "No requests on this page match that customer";

    protected override Task OnInitializedAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        isLoading = true;
        StateHasChanged();

        try
        {
            var cursor = currentPage - 1 < pageCursors.Count ? pageCursors[currentPage - 1] : null;
            var result = await Mediator.Send(
                new GetCreditNoteApprovalsQuery(statusFilter, currentPage, PageSize, cursor));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                SetRequests([]);
                totalCount = 0;
                nextCursor = null;
                return;
            }

            errorMessage = null;
            SetRequests(result.Value.Items);
            totalCount = result.Value.TotalCount;
            nextCursor = result.Value.NextCursor;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load SAP credit note approval requests");
            errorMessage = "The held credit notes could not be read from SAP.";
            SetRequests([]);
            totalCount = 0;
            nextCursor = null;
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private void SetRequests(List<CreditNoteApprovalListItemDto> loaded)
    {
        requests = loaded;
        visibleRequests = null;
    }

    /// <summary>
    /// Keeps the box in step with the typing but leaves the table alone until it stops. The version
    /// counter is the debounce: a keystroke arriving while this one waits bumps it, and the older
    /// call drops out rather than narrowing the rows to a term that has already been typed past.
    /// </summary>
    private async Task OnCustomerFilterInputAsync(ChangeEventArgs args)
    {
        customerFilter = args.Value?.ToString();
        var version = ++customerFilterVersion;

        // Clearing the box shows every row again straight away; there is nothing to wait for.
        if (string.IsNullOrWhiteSpace(customerFilter))
        {
            ApplyCustomerFilter();
            return;
        }

        await Task.Delay(FilterDebounce);
        if (customerFilterVersion != version)
        {
            return;
        }

        ApplyCustomerFilter();
    }

    private void ClearCustomerFilter()
    {
        customerFilter = null;
        customerFilterVersion++;
        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        appliedCustomerFilter = customerFilter;
        visibleRequests = null;
    }

    /// <summary>
    /// Back to the top of the queue, for a change of which queue is being read — a cursor taken
    /// under one status filter counts nothing under another.
    /// </summary>
    /// <remarks>
    /// Deciding a request does not need this, and neither does Refresh. The cursor is a boundary
    /// value rather than a pointer at a row: <c>Code lt 84120</c> still means the same place in the
    /// queue after 84120 has been approved and left the filter, so a manager keeps their page.
    /// </remarks>
    private void RestartPaging()
    {
        currentPage = 1;
        pageCursors.Clear();
        pageCursors.Add(null);
        nextCursor = null;
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task SetStatusFilterAsync(string status)
    {
        if (statusFilter == status)
        {
            return;
        }

        statusFilter = status;
        RestartPaging();
        await LoadAsync();
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
        {
            return;
        }

        // The cursor for the page being returned to is already held, so going back is a re-read of
        // the same window rather than a fresh count in from the top.
        currentPage--;
        await LoadAsync();
    }

    private async Task NextPageAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        // Where this page stopped becomes where the next one starts.
        if (pageCursors.Count == currentPage)
        {
            pageCursors.Add(nextCursor);
        }
        else
        {
            pageCursors[currentPage] = nextCursor;
        }

        currentPage++;
        await LoadAsync();
    }

    private async Task OpenAsync(int code)
    {
        detailCode = code;
        detail = null;
        detailError = null;
        attachmentError = null;
        decisionRemarks = null;
        isLoadingDetail = true;
        StateHasChanged();

        await LoadDetailAsync(code);
    }

    private async Task LoadDetailAsync(int code)
    {
        try
        {
            var result = await Mediator.Send(new GetCreditNoteApprovalQuery(code));

            // The drawer may have been closed, or opened on another row, while this was in flight.
            if (detailCode != code)
            {
                return;
            }

            if (result.IsError)
            {
                detail = null;
                detailError = result.FirstError.Description;
                return;
            }

            detail = result.Value;
            detailError = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load SAP credit note approval request {Code}", code);
            if (detailCode == code)
            {
                detail = null;
                detailError = "This request could not be read from SAP.";
            }
        }
        finally
        {
            if (detailCode == code)
            {
                isLoadingDetail = false;
                StateHasChanged();
            }
        }
    }

    private void CloseDetail()
    {
        detailCode = null;
        detail = null;
        detailError = null;
        attachmentError = null;
        decisionRemarks = null;
        isLoadingDetail = false;
        showAddConfirm = false;
    }

    private Task ApproveAsync() => DecideAsync("Approved", "approve");

    private Task RejectAsync() => DecideAsync("NotApproved", "reject");

    private async Task DecideAsync(string decision, string action)
    {
        if (detail is null || detailCode is not int code || isSubmitting)
        {
            return;
        }

        isSubmitting = true;
        submittingAction = action;
        StateHasChanged();

        try
        {
            // A new key per attempt: a retry of a call that timed out after SAP recorded the decision
            // replays its answer rather than reaching SAP twice.
            var result = await Mediator.Send(new DecideCreditNoteApprovalCommand(
                code, decision, decisionRemarks, Guid.NewGuid().ToString("N")));

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
                Snackbar.Add(result.FirstError.Description, Severity.Error);
            }
            else
            {
                detailError = null;
                decisionRemarks = null;
                Snackbar.Add(result.Value.Message, Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to decide SAP credit note approval request {Code}", code);
            detailError = "The decision could not be recorded in SAP.";
            Snackbar.Add(detailError, Severity.Error);
        }
        finally
        {
            isSubmitting = false;
            submittingAction = null;
        }

        // SAP is the source of truth for what the request is now, so re-read rather than assume —
        // but the row and the list are independent reads, and this is the manager's inner loop.
        // Awaiting one and then the other made every decision cost two waits instead of one.
        await Task.WhenAll(LoadDetailAsync(code), LoadAsync());
    }

    private void OpenAddConfirm() => showAddConfirm = true;

    private void CloseAddConfirm() => showAddConfirm = false;

    private async Task AddAsync()
    {
        if (detail is null || detailCode is not int code || isSubmitting)
        {
            return;
        }

        isSubmitting = true;
        StateHasChanged();

        try
        {
            var result = await Mediator.Send(new AddApprovedCreditNoteCommand(code, Guid.NewGuid().ToString("N")));

            if (result.IsError)
            {
                detailError = result.FirstError.Description;
                Snackbar.Add(result.FirstError.Description, Severity.Error);
            }
            else
            {
                detailError = null;
                showAddConfirm = false;
                Snackbar.Add(result.Value.Message, result.Value.Fiscalisation.Attempted && !result.Value.Fiscalisation.Success
                    ? Severity.Warning
                    : Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add the credit note for SAP approval request {Code}", code);
            detailError = "The credit note could not be added in SAP.";
            Snackbar.Add(detailError, Severity.Error);
        }
        finally
        {
            isSubmitting = false;
        }

        await Task.WhenAll(LoadDetailAsync(code), LoadAsync());
    }

    /// <summary>
    /// Fetches the file through the Web's own bearer proxy and shows it as a blob URL: the page's
    /// content security policy allows a blob iframe and a blob image, and no data: iframe at all.
    /// </summary>
    private async Task ViewAttachmentAsync(CreditNoteDraftAttachmentDto file)
    {
        if (detailCode is not int code)
        {
            return;
        }

        try
        {
            await RevokeViewerUrlAsync();

            attachmentError = null;
            viewerFile = file;
            viewerFileName = file.FileName;
            viewerMimeType = file.ContentType;
            showViewer = true;
            isLoadingViewer = true;
            StateHasChanged();

            viewerObjectUrl = await JS.InvokeAsync<string>(
                "createAuthenticatedObjectUrl",
                $"/download/credit-note-approval/{code}/{file.LineNum}");

            await TryAuditAsync(AuditActions.ViewSapCreditNoteAttachment, code, file, true, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to view attachment {LineNum} on approval request {Code}", file.LineNum, code);
            // The API says why — the file is not in the SAP attachments folder, the folder cannot be
            // reached, SAP refused the read — and that sentence is the only actionable thing here.
            attachmentError = JsInteropErrors.DescribeOrDefault(ex, "The attachment could not be opened.");
            Snackbar.Add(attachmentError, Severity.Error);
            showViewer = false;
            await TryAuditAsync(AuditActions.ViewSapCreditNoteAttachment, code, file, false, ex.Message);
        }
        finally
        {
            isLoadingViewer = false;
            StateHasChanged();
        }
    }

    private async Task DownloadAttachmentAsync(CreditNoteDraftAttachmentDto file)
    {
        if (detailCode is not int code)
        {
            return;
        }

        try
        {
            attachmentError = null;

            await JS.InvokeVoidAsync(
                "downloadAuthenticatedFile",
                $"/download/credit-note-approval/{code}/{file.LineNum}",
                file.FileName);

            await TryAuditAsync(AuditActions.DownloadSapCreditNoteAttachment, code, file, true, null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to download attachment {LineNum} on approval request {Code}", file.LineNum, code);
            attachmentError = JsInteropErrors.DescribeOrDefault(ex, "The attachment could not be downloaded.");
            Snackbar.Add(attachmentError, Severity.Error);
            await TryAuditAsync(AuditActions.DownloadSapCreditNoteAttachment, code, file, false, ex.Message);
        }
    }

    private async Task CloseViewerAsync()
    {
        showViewer = false;
        viewerFile = null;
        await RevokeViewerUrlAsync();
    }

    private async Task RevokeViewerUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(viewerObjectUrl))
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("revokeObjectUrl", viewerObjectUrl);
        }
        catch (Exception ex)
        {
            // The circuit may already be gone — the browser reclaims the URL with the page anyway.
            Logger.LogDebug(ex, "Could not revoke the attachment object URL");
        }
        finally
        {
            viewerObjectUrl = null;
        }
    }

    private async Task TryAuditAsync(string action, int code, CreditNoteDraftAttachmentDto file, bool success, string? error)
    {
        try
        {
            await AuditService.LogAsync(
                action,
                "SapApprovalRequest",
                code.ToString(),
                $"{action} '{file.FileName}' on SAP approval request {code}.",
                success,
                error);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to audit the attachment action on approval request {Code}", code);
        }
    }

    private string RowClass(CreditNoteApprovalListItemDto request)
    {
        var classes = new List<string>();
        if (detailCode == request.Code)
        {
            classes.Add("is-open-row");
        }

        if (!request.CanDecide && !request.CanAdd)
        {
            classes.Add("is-dim");
        }

        return string.Join(' ', classes);
    }

    /// <summary>The status word's own modifier class, shared by the dot and the text.</summary>
    private static string StatusModifier(string? status) => status?.ToLowerInvariant() switch
    {
        "pending" => "is-pending",
        "approved" => "is-approved",
        "notapproved" => "is-rejected",
        _ => string.Empty
    };

    private static string FileIcon(CreditNoteDraftAttachmentDto file) => file.ContentType switch
    {
        "application/pdf" => "ph ph-file-pdf",
        var type when type.StartsWith("image/", StringComparison.Ordinal) => "ph ph-image",
        _ => "ph ph-file"
    };

    private static string FormatDate(DateTime? value) => value?.ToString("dd MMM yyyy") ?? "—";

    private static string FormatMoney(decimal value, string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? value.ToString("N2") : $"{value:N2} {currency}";

    /// <summary>
    /// When the request was raised. The approval request carries its own creation date; the draft's
    /// document date is the fallback for one SAP recorded without it.
    /// </summary>
    private static DateTime? RaisedDate(CreditNoteApprovalListItemDto request) =>
        request.CreatedDate ?? request.DocDate;

    /// <summary>
    /// How long the request has been waiting, said the way the queue reads it. Blank rather than
    /// "0 days ago" when SAP gave no date, because an invented age is worse than none.
    /// </summary>
    private static string AgeText(DateTime? raised)
    {
        if (raised is null)
        {
            return string.Empty;
        }

        var days = (DateTime.UtcNow.Date - raised.Value.Date).Days;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            _ => $"{days} days ago"
        };
    }

    /// <summary>
    /// The request and draft numbers, folded under the customer name. They held their own columns
    /// before; the queue is read by customer, and the numbers are what you quote once you are in SAP.
    /// </summary>
    private static string RowMeta(CreditNoteApprovalListItemDto request)
    {
        var parts = new List<string> { $"#{request.Code}" };

        if (request.DraftNum is int draftNum)
        {
            parts.Add($"draft {draftNum}");
        }

        if (!string.IsNullOrWhiteSpace(request.CardCode))
        {
            parts.Add(request.CardCode);
        }

        return string.Join("  ·  ", parts);
    }

    public async ValueTask DisposeAsync() => await RevokeViewerUrlAsync();
}
