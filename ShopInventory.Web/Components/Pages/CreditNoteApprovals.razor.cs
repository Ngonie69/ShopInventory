using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;
using ShopInventory.Web.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;
using ShopInventory.Web.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovalCounts;
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

    /// <summary>
    /// The four queues, drawn as tiles across the top. Each is one status word the API's list route
    /// takes, so a tile and the rows it opens are the same read.
    /// </summary>
    /// <remarks>
    /// Awaiting a decision is the default rather than the API's own "open" (awaiting or approved):
    /// the page is a decision queue, and mixing the approved rows in buried the requests that still
    /// need somebody. Approved is one tile away.
    /// </remarks>
    private static readonly QueueTile[] Queues =
    [
        new("pending", "Awaiting decision", "is-pending", "Waiting for your decision"),
        new("approved", "Approved, not added", "is-approved", "Ready to post as credit notes"),
        new("rejected", "Rejected", "is-rejected", "Drafts stay in SAP for the originator"),
        new("all", "All requests", string.Empty, "Every status, added ones too")
    ];

    private sealed record QueueTile(string Value, string Label, string Modifier, string Hint);

    /// <summary>One step of the approval path the drawer draws.</summary>
    private sealed record PathStep(string Who, string Meta, string? Remark, string Modifier);

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

    private string statusFilter = "pending";

    /// <summary>How long each queue is, for the tiles. Null until SAP has answered.</summary>
    private CreditNoteApprovalCountsDto? counts;
    private bool countsFailed;

    /// <summary>When the list was last read from SAP, said in the header.</summary>
    private DateTime? listReadAt;

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

    /// <summary>
    /// What the last decision or add said, kept on the page. After an add SAP deletes the approval
    /// request, and after a decision the drawer moves on to the next request, so in neither case is
    /// the drawer still showing the request the message is about.
    /// </summary>
    private string? notice;
    private string? noticeSource;
    private bool noticeNeedsAttention;

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
            "pending" => "Nothing is waiting for a decision in SAP",
            "approved" => "Every approved credit memo has been added",
            "rejected" => "SAP holds no rejected credit memos",
            "all" => "SAP holds no credit memo approval requests",
            _ => "Nothing is waiting on a decision or an add"
        }
        : "No requests on this page match that customer";

    private string ListTitle => Queues.FirstOrDefault(queue => queue.Value == statusFilter)?.Label ?? "Requests";

    /// <summary>
    /// The number on a tile. The All tile has no count of its own — it would be a fourth count of the
    /// whole table — so it shows the list's total only while it is the queue being read.
    /// </summary>
    private string QueueCount(string queue)
    {
        int? value = queue switch
        {
            "pending" => counts?.Pending,
            "approved" => counts?.Approved,
            "rejected" => counts?.Rejected,
            _ => statusFilter == "all" && !isLoading ? totalCount : null
        };

        if (value is int known)
        {
            return known.ToString("N0");
        }

        return countsFailed || queue == "all" ? "—" : "…";
    }

    /// <summary>Where the open request sits in the rows on screen, or -1 when it is not among them.</summary>
    private int OpenIndex => detailCode is int code ? VisibleRequests.FindIndex(request => request.Code == code) : -1;

    private bool CanOpenPrevious => OpenIndex > 0 && !isSubmitting;

    private bool CanOpenNext => OpenIndex >= 0 && OpenIndex < VisibleRequests.Count - 1 && !isSubmitting;

    private string OpenPosition => OpenIndex >= 0 ? $"{OpenIndex + 1} of {VisibleRequests.Count}" : string.Empty;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        await LoadCountsAsync();
    }

    /// <summary>
    /// Reads the tiles' counts. After the list, never beside it: they are three more SAP reads, and
    /// the rows are what the person is waiting for. A failure leaves dashes on the tiles and the
    /// list as it is.
    /// </summary>
    private async Task LoadCountsAsync()
    {
        try
        {
            var result = await Mediator.Send(new GetCreditNoteApprovalCountsQuery());
            if (result.IsError)
            {
                Logger.LogWarning("Could not count the credit note approval queues: {Reason}", result.FirstError.Description);
                countsFailed = counts is null;
                return;
            }

            counts = result.Value;
            countsFailed = false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not count the credit note approval queues");
            countsFailed = counts is null;
        }
        finally
        {
            StateHasChanged();
        }
    }

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
            listReadAt = DateTime.Now;
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

    private async Task RefreshAsync()
    {
        await LoadAsync();
        await LoadCountsAsync();
    }

    private async Task SetStatusFilterAsync(string status)
    {
        if (statusFilter == status)
        {
            return;
        }

        // The open request belongs to the queue being left; it would be read against the wrong rows.
        CloseDetail();
        statusFilter = status;
        RestartPaging();
        await LoadAsync();
    }

    private async Task OpenPreviousAsync()
    {
        if (CanOpenPrevious)
        {
            await OpenAsync(VisibleRequests[OpenIndex - 1].Code);
        }
    }

    private async Task OpenNextAsync()
    {
        if (CanOpenNext)
        {
            await OpenAsync(VisibleRequests[OpenIndex + 1].Code);
        }
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

        // The request to move on to once this one is decided, taken from the rows as they stand now:
        // the reload below may drop this one from the queue, and with it any way to say what was next.
        var visible = VisibleRequests;
        var index = visible.FindIndex(request => request.Code == code);
        int? following = index < 0
            ? null
            : index + 1 < visible.Count ? visible[index + 1].Code
            : index > 0 ? visible[index - 1].Code
            : null;
        var cardName = detail.Request.CardName;
        var decided = false;

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
                decided = true;
                detailError = null;
                decisionRemarks = null;
                notice = result.Value.Message;
                noticeSource = string.IsNullOrWhiteSpace(cardName)
                    ? $"Approval request {code}"
                    : $"Approval request {code} · {cardName}";
                // An approval SAP recorded without approving the draft is not a success anybody can act on.
                noticeNeedsAttention = result.Value.Status == "Approved" && !result.Value.CanAdd;
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

        if (!decided)
        {
            // SAP is the source of truth for what the request is now, so re-read rather than assume —
            // the row and the list are independent reads, so they go together. The re-read clears
            // the drawer's error when the request reads back, so the refusal is put back after it,
            // where it stays rather than fading with the snackbar.
            var refusal = detailError;
            await Task.WhenAll(LoadDetailAsync(code), LoadAsync());
            if (refusal is not null && detailCode == code && detail is not null)
            {
                detailError = refusal;
            }

            return;
        }

        // Decided: what it said stays on the page, and the drawer moves on to the next request, so
        // working through the queue is one decision after another rather than open, decide, close.
        await LoadAsync();
        if (following is int next && VisibleRequests.Any(request => request.Code == next))
        {
            await OpenAsync(next);
        }
        else
        {
            CloseDetail();
        }

        await LoadCountsAsync();
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

        // Why the add was refused, kept past the reload below. The reason can be a paragraph saying
        // what to do instead (SAP's attachments folder, for one), and a snackbar is gone in seconds.
        string? refusal = null;
        AddApprovedCreditNoteResultDto? added = null;
        var cardName = detail.Request.CardName;

        try
        {
            var result = await Mediator.Send(new AddApprovedCreditNoteCommand(code, Guid.NewGuid().ToString("N")));

            if (result.IsError)
            {
                refusal = result.FirstError.Description;
                detailError = refusal;
                Snackbar.Add(refusal, Severity.Error);
            }
            else
            {
                added = result.Value;
                detailError = null;
                Snackbar.Add(added.Message, FiscalisationFailed(added) ? Severity.Warning : Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add the credit note for SAP approval request {Code}", code);
            refusal = "The credit note could not be added in SAP.";
            detailError = refusal;
            Snackbar.Add(refusal, Severity.Error);
        }
        finally
        {
            isSubmitting = false;
            // SAP has answered either way. Left open, the dialog covered the drawer and offered the
            // same Add again once the snackbar had faded.
            showAddConfirm = false;
        }

        if (added is not null)
        {
            // SAP deletes the approval request once it has turned the draft into the credit note, so
            // re-reading the request here answered "SAP approval request N was not found" and left that
            // error in the drawer under the success. The row leaves the queue too: close the drawer,
            // keep what the add said on the page, and re-read only the list.
            notice = added.Message;
            noticeSource = string.IsNullOrWhiteSpace(cardName)
                ? $"From approval request {code}"
                : $"From approval request {code} · {cardName}";
            noticeNeedsAttention = FiscalisationFailed(added);
            CloseDetail();
            await LoadAsync();
            await LoadCountsAsync();
            return;
        }

        await Task.WhenAll(LoadDetailAsync(code), LoadAsync());

        // The reload clears detailError when the request reads back. A request that could not be read
        // keeps the reload's own error instead, which says so.
        if (refusal is not null && detailCode == code && detail is not null)
        {
            detailError = refusal;
        }
    }

    private static bool FiscalisationFailed(AddApprovedCreditNoteResultDto added) =>
        added.Fiscalisation is { Attempted: true, Success: false };

    private void DismissNotice()
    {
        notice = null;
        noticeSource = null;
        noticeNeedsAttention = false;
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
    /// How long a request has been in the queue, said the way the queue reads it. Blank rather than
    /// "0 days" when SAP gave no date, because an invented age is worse than none. A request still
    /// waiting says it is waiting; a decided one says only when it was raised.
    /// </summary>
    private static string AgeText(CreditNoteApprovalListItemDto request)
    {
        if (DaysWaiting(request) is not int days)
        {
            return string.Empty;
        }

        if (IsPending(request.Status))
        {
            return days switch
            {
                <= 0 => "Raised today",
                1 => "Waiting since yesterday",
                _ => $"{days} days waiting"
            };
        }

        return days switch
        {
            <= 0 => "Raised today",
            1 => "Raised yesterday",
            _ => $"Raised {days} days ago"
        };
    }

    /// <summary>
    /// A pending request that has waited three days or more. The tile colour, not an alarm: the queue
    /// is worked oldest-first by eye, and this is what the eye should land on.
    /// </summary>
    private static bool IsLate(CreditNoteApprovalListItemDto request) =>
        IsPending(request.Status) && DaysWaiting(request) is >= 3;

    private static int? DaysWaiting(CreditNoteApprovalListItemDto request) =>
        RaisedDate(request) is DateTime raised ? (DateTime.Now.Date - raised.Date).Days : null;

    private static bool IsPending(string? status) =>
        string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>SAP's status word as the page says it. SAP's own word for a refusal is NotApproved.</summary>
    private static string StatusLabel(string? status) => status?.ToLowerInvariant() switch
    {
        "notapproved" => "Rejected",
        "generated" or "generatedbyauthorizer" => "Added",
        null or "" => "—",
        _ => status!
    };

    /// <summary>
    /// The request's way through SAP's approval procedure, top to bottom: who raised it, each approver
    /// line SAP holds, and the add still to come once it is approved.
    /// </summary>
    private static List<PathStep> ApprovalPath(CreditNoteApprovalDetailDto detail)
    {
        var request = detail.Request;
        var steps = new List<PathStep>
        {
            new(
                $"Raised by {request.OriginatorName ?? request.OriginatorUserCode ?? "an unknown user"}",
                FormatDate(RaisedDate(request)),
                null,
                "is-raised")
        };

        foreach (var decision in detail.Decisions)
        {
            var meta = $"{decision.StageName ?? $"Stage {decision.StageCode}"} · {StatusLabel(decision.Status)}";
            if (decision.DecidedDate is not null)
            {
                meta += $" · {FormatDate(decision.DecidedDate)}";
            }

            steps.Add(new(
                decision.UserName ?? decision.UserCode ?? $"User {decision.UserId}",
                meta,
                string.IsNullOrWhiteSpace(decision.Remarks) ? null : decision.Remarks,
                StatusModifier(decision.Status)));
        }

        if (request.CanAdd)
        {
            steps.Add(new("Add to SAP", "Posts the credit note and sends it for fiscalisation", null, string.Empty));
        }

        return steps;
    }

    public async ValueTask DisposeAsync() => await RevokeViewerUrlAsync();
}
