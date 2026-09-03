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
    private int totalCount;
    private int currentPage = 1;
    private bool isLoading = true;
    private string? errorMessage;

    private string statusFilter = "open";
    private string? customerFilter;

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

    /// <summary>The customer filter narrows the page in hand; SAP already answered for this page.</summary>
    private List<CreditNoteApprovalListItemDto> VisibleRequests =>
        string.IsNullOrWhiteSpace(customerFilter)
            ? requests
            : requests.Where(request =>
                (request.CardCode?.Contains(customerFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (request.CardName?.Contains(customerFilter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

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

    private string EmptyMessage => string.IsNullOrWhiteSpace(customerFilter)
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
            var result = await Mediator.Send(new GetCreditNoteApprovalsQuery(statusFilter, currentPage, PageSize));
            if (result.IsError)
            {
                errorMessage = result.FirstError.Description;
                requests = [];
                totalCount = 0;
                return;
            }

            errorMessage = null;
            requests = result.Value.Items;
            totalCount = result.Value.TotalCount;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load SAP credit note approval requests");
            errorMessage = "The held credit notes could not be read from SAP.";
            requests = [];
            totalCount = 0;
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private Task RefreshAsync() => LoadAsync();

    private async Task SetStatusFilterAsync(string status)
    {
        if (statusFilter == status)
        {
            return;
        }

        statusFilter = status;
        currentPage = 1;
        await LoadAsync();
    }

    private async Task PreviousPageAsync()
    {
        if (currentPage <= 1)
        {
            return;
        }

        currentPage--;
        await LoadAsync();
    }

    private async Task NextPageAsync()
    {
        if (currentPage >= TotalPages)
        {
            return;
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

        // SAP is the source of truth for what the request is now, so re-read rather than assume.
        await LoadDetailAsync(code);
        await LoadAsync();
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

        await LoadDetailAsync(code);
        await LoadAsync();
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

    public async ValueTask DisposeAsync() => await RevokeViewerUrlAsync();
}
