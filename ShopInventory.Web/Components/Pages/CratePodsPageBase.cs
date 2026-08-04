using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

public abstract class CratePodsPageBase : CrateTrackingPageBase
{
    protected static readonly int[] CratePageSizeOptions = [25, 50, 100];

    private string transactionFilterSearch = string.Empty;
    private string transactionFilterStatus = "all";
    private string transactionFilterShopCode = string.Empty;
    private DateTime? transactionFilterFromDate;
    private DateTime? transactionFilterToDate;
    private int transactionPage = 1;
    private int transactionPageSize = 25;

    private string podHistorySearch = string.Empty;
    private string podHistoryRoleFilter = "all";
    private string podHistoryShopCode = string.Empty;
    private string podHistoryUploadedBy = string.Empty;
    private DateTime? podHistoryFromDate;
    private DateTime? podHistoryToDate;
    private int podHistoryPage = 1;
    private int podHistoryPageSize = 25;

    protected CratePodUploadMode uploadMode = CratePodUploadMode.Single;
    protected int bulkPodFileKey;
    protected readonly List<BulkCrateFileMapping> crateBulkMappings = [];
    protected string? bulkPodNotes;
    protected bool isBulkValidatingPods;
    protected string? bulkValidationProgress;
    protected bool isBulkUploadingPods;
    protected string? bulkUploadProgress;
    protected string? bulkUploadMessage;
    protected bool bulkUploadSuccess;
    protected string? bulkNamingGuideMessage;

    protected bool showAttachmentViewer;
    protected bool isLoadingAttachmentViewer;
    protected string? attachmentViewerUrl;
    protected string? attachmentViewerFileName;
    protected string? attachmentViewerMimeType;
    protected DocumentAttachmentDto? attachmentViewer;

    protected bool showDeleteAttachmentConfirm;
    protected bool isDeletingAttachment;
    protected CratePodSubmissionDto? podToDelete;
    protected DocumentAttachmentDto? attachmentToDelete;
    protected string? attachmentDeleteContext;

    protected enum CratePodUploadMode
    {
        Single,
        Bulk
    }

    protected enum BulkCrateFileStatus
    {
        Pending,
        Valid,
        ValidWithWarning,
        Uploading,
        Uploaded,
        Error
    }

    protected sealed class BulkCrateFileMapping
    {
        public IBrowserFile File { get; init; } = default!;
        public string FileName { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public int? InvoiceDocNum { get; set; }
        public string? SubmissionRole { get; set; }
        public int? CrateTransactionId { get; set; }
        public string? ShopCardCode { get; set; }
        public string? ShopName { get; set; }
        public decimal ExpectedQuantity { get; set; }
        public decimal Quantity { get; set; }
        public bool HasExistingSubmission { get; set; }
        public int ExistingAttachmentCount { get; set; }
        public BulkCrateFileStatus Status { get; set; } = BulkCrateFileStatus.Pending;
        public string? ErrorMessage { get; set; }
    }

    // Every transaction filter resets the pager, otherwise a narrowed list lands the user on an empty page.
    protected string TransactionFilterSearch
    {
        get => transactionFilterSearch;
        set => SetTransactionFilter(ref transactionFilterSearch, value);
    }

    protected string TransactionFilterStatus
    {
        get => transactionFilterStatus;
        set => SetTransactionFilter(ref transactionFilterStatus, value);
    }

    protected string TransactionFilterShopCode
    {
        get => transactionFilterShopCode;
        set => SetTransactionFilter(ref transactionFilterShopCode, value);
    }

    protected DateTime? TransactionFilterFromDate
    {
        get => transactionFilterFromDate;
        set => SetTransactionFilter(ref transactionFilterFromDate, value);
    }

    protected DateTime? TransactionFilterToDate
    {
        get => transactionFilterToDate;
        set => SetTransactionFilter(ref transactionFilterToDate, value);
    }

    protected int TransactionPageSize
    {
        get => transactionPageSize;
        set
        {
            transactionPageSize = CratePageSizeOptions.Contains(value) ? value : CratePageSizeOptions[0];
            transactionPage = 1;
        }
    }

    protected int TransactionPage => transactionPage;

    protected bool HasTransactionFilters =>
        !string.IsNullOrWhiteSpace(transactionFilterSearch) ||
        !string.IsNullOrWhiteSpace(transactionFilterShopCode) ||
        !string.Equals(transactionFilterStatus, "all", StringComparison.OrdinalIgnoreCase) ||
        transactionFilterFromDate.HasValue ||
        transactionFilterToDate.HasValue;

    protected IEnumerable<CrateTransactionDto> FilteredPodTransactions => PodEligibleTransactions
        .Where(MatchesTransactionFilters);

    protected void ClearTransactionFilters()
    {
        transactionFilterSearch = string.Empty;
        transactionFilterShopCode = string.Empty;
        transactionFilterStatus = "all";
        transactionFilterFromDate = null;
        transactionFilterToDate = null;
        transactionPage = 1;
    }

    /// <summary>
    /// Clamps the current page to the available range and returns it. Called while rendering,
    /// so the pager never points past the end after a filter or a refresh shrinks the list.
    /// </summary>
    protected int ResolveTransactionPage(int totalPages)
    {
        transactionPage = Math.Clamp(transactionPage, 1, Math.Max(totalPages, 1));
        return transactionPage;
    }

    protected void GoToTransactionPage(int page)
    {
        transactionPage = Math.Max(page, 1);
    }

    private void SetTransactionFilter<T>(ref T field, T value)
    {
        field = value;
        transactionPage = 1;
    }

    // Same pager contract as the transaction list: every filter write returns to page one.
    protected string PodHistorySearch
    {
        get => podHistorySearch;
        set => SetPodHistoryFilter(ref podHistorySearch, value);
    }

    protected string PodHistoryRoleFilter
    {
        get => podHistoryRoleFilter;
        set => SetPodHistoryFilter(ref podHistoryRoleFilter, value);
    }

    protected string PodHistoryShopCode
    {
        get => podHistoryShopCode;
        set => SetPodHistoryFilter(ref podHistoryShopCode, value);
    }

    protected string PodHistoryUploadedBy
    {
        get => podHistoryUploadedBy;
        set => SetPodHistoryFilter(ref podHistoryUploadedBy, value);
    }

    protected DateTime? PodHistoryFromDate
    {
        get => podHistoryFromDate;
        set => SetPodHistoryFilter(ref podHistoryFromDate, value);
    }

    protected DateTime? PodHistoryToDate
    {
        get => podHistoryToDate;
        set => SetPodHistoryFilter(ref podHistoryToDate, value);
    }

    protected int PodHistoryPageSize
    {
        get => podHistoryPageSize;
        set
        {
            podHistoryPageSize = CratePageSizeOptions.Contains(value) ? value : CratePageSizeOptions[0];
            podHistoryPage = 1;
        }
    }

    protected IEnumerable<CratePodSubmissionDto> FilteredPodHistory => pods
        .Where(MatchesPodHistoryFilters)
        .OrderByDescending(pod => pod.SubmittedAt)
        .ThenByDescending(pod => pod.InvoiceDocNum);

    /// <summary>
    /// One row per uploaded file, so the history reads like the product POD upload list.
    /// Submissions without documents still get a row, otherwise they would vanish from the page.
    /// </summary>
    protected IEnumerable<CratePodAttachmentRow> FilteredPodAttachmentRows => FilteredPodHistory
        .SelectMany(pod => pod.Attachments.Count == 0
            ? Enumerable.Repeat(new CratePodAttachmentRow(pod, null), 1)
            : pod.Attachments
                .OrderByDescending(attachment => attachment.UploadedAt)
                .Select(attachment => new CratePodAttachmentRow(pod, attachment)));

    protected bool HasPodHistoryFilters =>
        !string.IsNullOrWhiteSpace(podHistorySearch) ||
        !string.IsNullOrWhiteSpace(podHistoryShopCode) ||
        !string.IsNullOrWhiteSpace(podHistoryUploadedBy) ||
        !string.Equals(podHistoryRoleFilter, "all", StringComparison.OrdinalIgnoreCase) ||
        podHistoryFromDate.HasValue ||
        podHistoryToDate.HasValue;

    protected sealed record CratePodAttachmentRow(CratePodSubmissionDto Pod, DocumentAttachmentDto? Attachment);

    protected void ClearPodHistoryFilters()
    {
        podHistorySearch = string.Empty;
        podHistoryShopCode = string.Empty;
        podHistoryUploadedBy = string.Empty;
        podHistoryRoleFilter = "all";
        podHistoryFromDate = null;
        podHistoryToDate = null;
        podHistoryPage = 1;
    }

    /// <summary>
    /// Clamps the current page to the available range and returns it, so deleting the last
    /// document on a page cannot strand the user past the end of the list.
    /// </summary>
    protected int ResolvePodHistoryPage(int totalPages)
    {
        podHistoryPage = Math.Clamp(podHistoryPage, 1, Math.Max(totalPages, 1));
        return podHistoryPage;
    }

    protected void GoToPodHistoryPage(int page)
    {
        podHistoryPage = Math.Max(page, 1);
    }

    private void SetPodHistoryFilter<T>(ref T field, T value)
    {
        field = value;
        podHistoryPage = 1;
    }

    protected CrateTransactionDto? SelectedPodTransaction => selectedPodTransactionId.HasValue
        ? PodEligibleTransactions.FirstOrDefault(transaction => transaction.Id == selectedPodTransactionId.Value)
        : null;

    protected int CrateBulkReadyCount => crateBulkMappings.Count(mapping =>
        mapping.Status is BulkCrateFileStatus.Valid or BulkCrateFileStatus.ValidWithWarning);

    protected string CurrentPodRoleLabel => string.Equals(selectedPodRole, "Merchandiser", StringComparison.OrdinalIgnoreCase)
        ? "Merchandiser"
        : "Driver";

    protected void SwitchUploadMode(CratePodUploadMode mode)
    {
        uploadMode = mode;
        bulkUploadMessage = null;
        bulkUploadSuccess = false;
        ClearStatus();
    }

    // The dropdowns on these pages are NocturneSelect, so the option lists are
    // built here rather than as <option>s. "all" rather than an empty string is
    // the no-filter sentinel, so those rows carry IsUnset and their triggers
    // stay in the resting state until a filter is actually chosen.
    protected static readonly NocturneSelectOption<int>[] CratePageSizeSelectOptions =
        CratePageSizeOptions.Select(size => new NocturneSelectOption<int>(size, size.ToString())).ToArray();

    protected static readonly NocturneSelectOption<string>[] PodRoleSelectOptions =
        [new("Driver", "Driver", "info"), new("Merchandiser", "Merchandiser", "accent")];

    protected static readonly NocturneSelectOption<string>[] TransactionStatusFilterOptions =
    [
        new("all", "All statuses", "neutral") { IsUnset = true, RuleAfter = true },
        new("PendingDriverPod", "Pending Driver POD", "warn"),
        new("PendingMerchandiserPod", "Pending Merch POD", "warn"),
        new("Matched", "Matched", "good"),
        new("VariancePendingGrv", "Variance Pending GRV", "bad"),
        new("GrvRaised", "GRV Raised", "info")
    ];

    protected static readonly NocturneSelectOption<string>[] PodHistoryRoleFilterOptions =
    [
        new("all", "All roles", "neutral") { IsUnset = true, RuleAfter = true },
        new("Driver", "Driver", "info"),
        new("Merchandiser", "Merchandiser", "accent")
    ];

    // GetTransactionLabel is the same helper the results table labels a row
    // with. 0 stands for "nothing chosen" because the bound field is an int?.
    protected IEnumerable<NocturneSelectOption<int>> PodTransactionSelectOptions =>
        PodEligibleTransactions
            .Select(t => new NocturneSelectOption<int>(t.Id, GetTransactionLabel(t)))
            .Prepend(new NocturneSelectOption<int>(0, "Select a crate transaction", "neutral")
            {
                IsUnset = true,
                RuleAfter = true
            });

    protected void OnSelectedPodTransactionChanged(int transactionId)
    {
        if (transactionId > 0)
        {
            SelectPodTransaction(transactionId);
            return;
        }

        selectedPodTransactionId = null;
        podQuantity = 0;
        ClearStatus();
    }

    protected void OnSelectedPodRoleChanged(string? role)
    {
        selectedPodRole = string.Equals(role, "Merchandiser", StringComparison.OrdinalIgnoreCase)
            ? "Merchandiser"
            : "Driver";

        if (SelectedPodTransaction is not null)
        {
            podQuantity = GetSuggestedPodQuantity(SelectedPodTransaction);
        }

        ClearStatus();
    }

    protected void SelectPodTransaction(int transactionId)
    {
        selectedPodTransactionId = transactionId;

        if (SelectedPodTransaction is not null)
        {
            podQuantity = GetSuggestedPodQuantity(SelectedPodTransaction);
        }

        ClearStatus();
    }

    protected decimal GetSuggestedPodQuantity(CrateTransactionDto transaction)
    {
        return string.Equals(selectedPodRole, "Merchandiser", StringComparison.OrdinalIgnoreCase)
            ? transaction.MerchandiserQuantity ?? transaction.ExpectedQuantity
            : transaction.DriverQuantity ?? transaction.ExpectedQuantity;
    }

    protected void OnBulkPodFilesSelected(InputFileChangeEventArgs e)
    {
        crateBulkMappings.Clear();

        foreach (var file in e.GetMultipleFiles(100))
        {
            var invoiceDocNum = TryExtractInvoiceDocNum(file.Name);
            var submissionRole = TryInferSubmissionRole(file.Name);
            var hasInvoiceDocNum = invoiceDocNum.HasValue;
            var hasSubmissionRole = !string.IsNullOrWhiteSpace(submissionRole);

            crateBulkMappings.Add(new BulkCrateFileMapping
            {
                File = file,
                FileName = file.Name,
                FileSize = file.Size,
                InvoiceDocNum = invoiceDocNum,
                SubmissionRole = submissionRole,
                Status = hasInvoiceDocNum && hasSubmissionRole ? BulkCrateFileStatus.Pending : BulkCrateFileStatus.Error,
                ErrorMessage = GetBulkFileNamingError(invoiceDocNum, submissionRole, file.Name)
            });
        }

        bulkUploadMessage = null;
        bulkUploadSuccess = false;
        bulkNamingGuideMessage = null;
    }

    protected async Task CopyBulkNamingExamplesAsync()
    {
        const string exampleText = "10001_driver.jpg\n10001_drv_signed.png\n10002_merch.pdf\n10002_merchandiser_sheet.webp";

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", exampleText);
            bulkNamingGuideMessage = "Example filenames copied to clipboard.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to copy crate bulk naming examples");
            bulkNamingGuideMessage = "Unable to copy the example filenames right now.";
        }
    }

    protected async Task ValidateBulkPodFilesAsync()
    {
        if (isBulkValidatingPods)
        {
            return;
        }

        var pendingMappings = crateBulkMappings
            .Where(mapping => mapping.Status != BulkCrateFileStatus.Uploaded)
            .ToList();

        if (!pendingMappings.Any())
        {
            return;
        }

        isBulkValidatingPods = true;
        bulkValidationProgress = null;
        bulkUploadMessage = null;
        bulkUploadSuccess = false;

        try
        {
            await ValidateBulkPodFilesCoreAsync(pendingMappings);
            UpdateBulkValidationSummary();
        }
        finally
        {
            isBulkValidatingPods = false;
            bulkValidationProgress = null;
        }
    }

    protected async Task RetryBulkPodValidationAsync()
    {
        if (isBulkValidatingPods)
        {
            return;
        }

        var errorMappings = crateBulkMappings
            .Where(mapping => mapping.Status == BulkCrateFileStatus.Error && mapping.InvoiceDocNum.HasValue)
            .ToList();

        if (!errorMappings.Any())
        {
            return;
        }

        foreach (var mapping in errorMappings)
        {
            ResetBulkMappingValidation(mapping);
            mapping.Status = BulkCrateFileStatus.Pending;
            mapping.ErrorMessage = null;
        }

        isBulkValidatingPods = true;
        bulkValidationProgress = null;
        bulkUploadMessage = null;
        bulkUploadSuccess = false;

        try
        {
            await ValidateBulkPodFilesCoreAsync(errorMappings);
            UpdateBulkValidationSummary();
        }
        finally
        {
            isBulkValidatingPods = false;
            bulkValidationProgress = null;
        }
    }

    protected async Task ValidateBulkPodFilesCoreAsync(IReadOnlyList<BulkCrateFileMapping> mappings)
    {
        var resultsByInvoiceAndRole = new Dictionary<string, BulkCratePodValidationResult>(StringComparer.OrdinalIgnoreCase);
        var validationFailedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groups = mappings
            .Where(mapping => mapping.InvoiceDocNum.HasValue && !string.IsNullOrWhiteSpace(mapping.SubmissionRole))
            .GroupBy(mapping => mapping.SubmissionRole!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var invoiceDocNums = group
                .Select(mapping => mapping.InvoiceDocNum!.Value)
                .Distinct()
                .ToList();

            if (invoiceDocNums.Count == 0)
            {
                continue;
            }

            var response = await CrateTrackingService.ValidateBulkCratePodsAsync(invoiceDocNums, group.Key);
            if (response is null)
            {
                validationFailedGroups.Add(group.Key);
                continue;
            }

            foreach (var result in response.Results)
            {
                resultsByInvoiceAndRole[BuildValidationKey(result.InvoiceDocNum, group.Key)] = result;
            }
        }

        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            bulkValidationProgress = $"({index + 1}/{mappings.Count})";
            await InvokeAsync(StateHasChanged);

            ResetBulkMappingValidation(mapping);

            if (!mapping.InvoiceDocNum.HasValue)
            {
                mapping.Status = BulkCrateFileStatus.Error;
                mapping.ErrorMessage = "Filename must start with the invoice number.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.SubmissionRole))
            {
                mapping.Status = BulkCrateFileStatus.Error;
                mapping.ErrorMessage = GetBulkFileNamingError(mapping.InvoiceDocNum, mapping.SubmissionRole, mapping.FileName);
                continue;
            }

            if (validationFailedGroups.Contains(mapping.SubmissionRole))
            {
                mapping.Status = BulkCrateFileStatus.Error;
                mapping.ErrorMessage = $"Bulk validation failed for {mapping.SubmissionRole} files. Please try again.";
                continue;
            }

            if (!resultsByInvoiceAndRole.TryGetValue(BuildValidationKey(mapping.InvoiceDocNum.Value, mapping.SubmissionRole), out var result) || !result.Found)
            {
                mapping.Status = BulkCrateFileStatus.Error;
                mapping.ErrorMessage = result?.ErrorMessage ?? $"Invoice #{mapping.InvoiceDocNum} could not be matched to a crate transaction.";
                continue;
            }

            if (!result.CanUpload || !result.CrateTransactionId.HasValue)
            {
                mapping.Status = BulkCrateFileStatus.Error;
                mapping.ErrorMessage = result.ErrorMessage ?? $"This file cannot be uploaded as a {mapping.SubmissionRole} crate POD.";
                continue;
            }

            mapping.CrateTransactionId = result.CrateTransactionId;
            mapping.ShopCardCode = result.ShopCardCode;
            mapping.ShopName = result.ShopName;
            mapping.ExpectedQuantity = result.ExpectedQuantity;
            mapping.Quantity = mapping.Quantity > 0 ? mapping.Quantity : result.ExistingQuantity ?? result.ExpectedQuantity;
            mapping.HasExistingSubmission = result.HasExistingSubmission;
            mapping.ExistingAttachmentCount = result.ExistingAttachmentCount;
            mapping.Status = result.HasExistingSubmission || result.ExistingAttachmentCount > 0
                ? BulkCrateFileStatus.ValidWithWarning
                : BulkCrateFileStatus.Valid;
        }
    }

    protected async Task ExecuteBulkPodUploadAsync()
    {
        if (isBulkUploadingPods)
        {
            return;
        }

        var readyMappings = crateBulkMappings
            .Where(mapping => mapping.Status is BulkCrateFileStatus.Valid or BulkCrateFileStatus.ValidWithWarning)
            .ToList();

        if (!readyMappings.Any())
        {
            return;
        }

        isBulkUploadingPods = true;
        bulkUploadProgress = null;
        bulkUploadMessage = null;
        bulkUploadSuccess = false;

        var successCount = 0;
        var errorCount = 0;

        try
        {
            for (var index = 0; index < readyMappings.Count; index++)
            {
                var mapping = readyMappings[index];
                bulkUploadProgress = $"({index + 1}/{readyMappings.Count})";
                mapping.Status = BulkCrateFileStatus.Uploading;
                await InvokeAsync(StateHasChanged);

                if (!mapping.CrateTransactionId.HasValue)
                {
                    mapping.Status = BulkCrateFileStatus.Error;
                    mapping.ErrorMessage = "This file must be validated before upload.";
                    errorCount++;
                    continue;
                }

                var quantity = mapping.Quantity > 0 ? mapping.Quantity : mapping.ExpectedQuantity;
                var (success, message, _) = await CrateTrackingService.UploadCratePodAsync(
                    mapping.CrateTransactionId.Value,
                    quantity,
                    mapping.File,
                    mapping.SubmissionRole,
                    bulkPodNotes);

                if (success)
                {
                    mapping.Status = BulkCrateFileStatus.Uploaded;
                    mapping.ErrorMessage = null;
                    successCount++;
                }
                else
                {
                    mapping.Status = BulkCrateFileStatus.Error;
                    mapping.ErrorMessage = message;
                    errorCount++;
                }
            }

            if (successCount > 0)
            {
                await RefreshAllAsync();
            }

            bulkUploadSuccess = successCount > 0 && errorCount == 0;
            bulkUploadMessage = errorCount == 0
                ? $"{successCount} crate POD file(s) uploaded successfully."
                : successCount > 0
                    ? $"{successCount} file(s) uploaded. {errorCount} file(s) still need attention."
                    : "No crate POD files were uploaded.";

            if (successCount > 0 && errorCount == 0)
            {
                ClearCrateBulkUploadState(clearMessage: false);
            }
        }
        finally
        {
            isBulkUploadingPods = false;
            bulkUploadProgress = null;
        }
    }

    protected void ClearCrateBulkUpload()
    {
        ClearCrateBulkUploadState(clearMessage: true);
    }

    protected void ClearCrateBulkUploadState(bool clearMessage)
    {
        crateBulkMappings.Clear();
        bulkPodNotes = null;
        bulkPodFileKey++;

        if (clearMessage)
        {
            bulkUploadMessage = null;
            bulkUploadSuccess = false;
        }
    }

    protected void RemoveCrateBulkMapping(BulkCrateFileMapping mapping)
    {
        crateBulkMappings.Remove(mapping);

        if (!crateBulkMappings.Any())
        {
            bulkUploadMessage = null;
            bulkUploadSuccess = false;
        }
        else
        {
            UpdateBulkValidationSummary();
        }
    }

    protected static void ResetBulkMappingValidation(BulkCrateFileMapping mapping)
    {
        mapping.CrateTransactionId = null;
        mapping.ShopCardCode = null;
        mapping.ShopName = null;
        mapping.ExpectedQuantity = 0;
        mapping.Quantity = 0;
        mapping.HasExistingSubmission = false;
        mapping.ExistingAttachmentCount = 0;
    }

    protected void UpdateBulkValidationSummary()
    {
        var readyCount = CrateBulkReadyCount;
        var errorCount = crateBulkMappings.Count(mapping => mapping.Status == BulkCrateFileStatus.Error);

        if (readyCount == 0 && errorCount == 0)
        {
            bulkUploadMessage = null;
            bulkUploadSuccess = false;
            return;
        }

        bulkUploadSuccess = readyCount > 0 && errorCount == 0;
        bulkUploadMessage = readyCount > 0 && errorCount == 0
            ? $"{readyCount} file(s) ready for upload."
            : readyCount > 0
                ? $"{readyCount} file(s) ready, {errorCount} need attention."
                : $"{errorCount} file(s) need attention before upload.";
    }

    protected async Task ViewAttachmentAsync(DocumentAttachmentDto attachment)
    {
        try
        {
            await CloseAttachmentViewerAsync();

            attachmentViewer = attachment;
            attachmentViewerFileName = attachment.FileName;
            attachmentViewerMimeType = attachment.MimeType;
            showAttachmentViewer = true;
            isLoadingAttachmentViewer = true;
            await InvokeAsync(StateHasChanged);

            attachmentViewerUrl = await JS.InvokeAsync<string>(
                "createAuthenticatedObjectUrl",
                attachment.DownloadUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to preview crate attachment {AttachmentId}", attachment.Id);
            showAttachmentViewer = false;
            attachmentViewer = null;
            attachmentViewerUrl = null;
            SetStatus(false, "The document preview could not be opened.");
        }
        finally
        {
            isLoadingAttachmentViewer = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    protected async Task CloseAttachmentViewerAsync()
    {
        showAttachmentViewer = false;

        if (!string.IsNullOrWhiteSpace(attachmentViewerUrl))
        {
            await JS.InvokeVoidAsync("revokeObjectUrl", attachmentViewerUrl);
            attachmentViewerUrl = null;
        }

        attachmentViewer = null;
        attachmentViewerFileName = null;
        attachmentViewerMimeType = null;
    }

    protected void BeginDeleteAttachment(CratePodSubmissionDto pod, DocumentAttachmentDto? attachment)
    {
        if (!canDeleteCratePods)
        {
            return;
        }

        podToDelete = pod;
        attachmentToDelete = attachment;
        attachmentDeleteContext = $"{GetPodReferenceLabel(pod)} {pod.SubmissionRole} submission";
        showDeleteAttachmentConfirm = true;
    }

    protected void CancelDeleteAttachment()
    {
        showDeleteAttachmentConfirm = false;
        podToDelete = null;
        attachmentToDelete = null;
        attachmentDeleteContext = null;
    }

    protected async Task DeletePodAsync()
    {
        if (!canDeleteCratePods || podToDelete is null || isDeletingAttachment)
        {
            return;
        }

        isDeletingAttachment = true;

        try
        {
            var deletedAttachmentId = attachmentToDelete?.Id;
            var deletedPodId = podToDelete.Id;
            var (success, message) = await CrateTrackingService.DeletePodAsync(deletedPodId);

            if (success)
            {
                if (deletedAttachmentId.HasValue && attachmentViewer?.Id == deletedAttachmentId.Value)
                {
                    await CloseAttachmentViewerAsync();
                }

                CancelDeleteAttachment();
                await RefreshAllAsync();
                SetStatus(true, message);
            }
            else
            {
                SetStatus(false, message);
            }
        }
        finally
        {
            isDeletingAttachment = false;
        }
    }

    protected bool MatchesTransactionFilters(CrateTransactionDto transaction)
    {
        if (!string.IsNullOrWhiteSpace(transactionFilterStatus) &&
            !string.Equals(transactionFilterStatus, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(transaction.Status, transactionFilterStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(transactionFilterShopCode) &&
            !transaction.ShopCardCode.Contains(transactionFilterShopCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (transactionFilterFromDate.HasValue && transaction.EffectiveDate.Date < transactionFilterFromDate.Value.Date)
        {
            return false;
        }

        if (transactionFilterToDate.HasValue && transaction.EffectiveDate.Date > transactionFilterToDate.Value.Date)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(transactionFilterSearch))
        {
            return true;
        }

        var term = transactionFilterSearch.Trim();
        return (transaction.InvoiceDocNum?.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               transaction.ShopCardCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               (transaction.ShopName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               GetStatusLabel(transaction.Status).Contains(term, StringComparison.OrdinalIgnoreCase) ||
               (transaction.Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    protected bool MatchesPodHistoryFilters(CratePodSubmissionDto pod)
    {
        if (!string.IsNullOrWhiteSpace(podHistoryRoleFilter) &&
            !string.Equals(podHistoryRoleFilter, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(pod.SubmissionRole, podHistoryRoleFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(podHistoryShopCode) &&
            !pod.ShopCardCode.Contains(podHistoryShopCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(podHistoryUploadedBy) &&
            (pod.SubmittedByUserName?.Contains(podHistoryUploadedBy.Trim(), StringComparison.OrdinalIgnoreCase) != true))
        {
            return false;
        }

        if (podHistoryFromDate.HasValue && pod.SubmittedAt.Date < podHistoryFromDate.Value.Date)
        {
            return false;
        }

        if (podHistoryToDate.HasValue && pod.SubmittedAt.Date > podHistoryToDate.Value.Date)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(podHistorySearch))
        {
            return true;
        }

        var term = podHistorySearch.Trim();
        return (pod.InvoiceDocNum?.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               pod.ShopCardCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               (pod.ShopName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (pod.SubmittedByUserName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (pod.Notes?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
               pod.Attachments.Any(attachment => attachment.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nocturne pill modifier for a crate transaction's status. "Matched" is the settled,
    /// nothing-to-do state; the GRV states are the ones someone has to act on.
    /// </summary>
    protected static string GetStatusPillClass(string status)
    {
        return status switch
        {
            "Matched" => "is-ok",
            "GrvRaised" => "is-bad",
            "VariancePendingGrv" => "is-bad",
            _ => "is-warn"
        };
    }

    /// <summary>
    /// Row modifier for a bulk-mapping row, mirroring the product POD table: a failed row is
    /// the one being scanned for, an uploaded or skipped row recedes.
    /// </summary>
    protected static string? GetCrateBulkRowClass(BulkCrateFileStatus status)
    {
        return status switch
        {
            BulkCrateFileStatus.Error => "is-bad",
            BulkCrateFileStatus.Uploaded => "is-done",
            _ => null
        };
    }

    protected static bool CanPreviewAttachment(string? mimeType)
    {
        return mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
               string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    protected static string BuildValidationKey(int invoiceDocNum, string submissionRole)
    {
        return $"{invoiceDocNum}:{submissionRole.Trim()}";
    }

    protected static int? TryExtractInvoiceDocNum(string fileName)
    {
        var match = Regex.Match(fileName ?? string.Empty, @"^\s*(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var invoiceDocNum)
            ? invoiceDocNum
            : null;
    }

    protected static string? TryInferSubmissionRole(string? fileName)
    {
        var fileNameValue = fileName ?? string.Empty;
        var hasDriverMarker = Regex.IsMatch(fileNameValue, @"(?:^|[^a-z0-9])(driver|drv)(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase);
        var hasMerchMarker = Regex.IsMatch(fileNameValue, @"(?:^|[^a-z0-9])(merch|merchandiser)(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase);

        return (hasDriverMarker, hasMerchMarker) switch
        {
            (true, false) => "Driver",
            (false, true) => "Merchandiser",
            _ => null
        };
    }

    protected static string GetBulkFileNamingError(int? invoiceDocNum, string? submissionRole, string fileName)
    {
        if (!invoiceDocNum.HasValue)
        {
            return "Filename must start with the invoice number.";
        }

        var fileNameValue = fileName ?? string.Empty;
        var hasDriverMarker = Regex.IsMatch(fileNameValue, @"(?:^|[^a-z0-9])(driver|drv)(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase);
        var hasMerchMarker = Regex.IsMatch(fileNameValue, @"(?:^|[^a-z0-9])(merch|merchandiser)(?:[^a-z0-9]|$)", RegexOptions.IgnoreCase);

        if (hasDriverMarker && hasMerchMarker)
        {
            return "Filename contains both driver and merchandiser role markers.";
        }

        if (string.IsNullOrWhiteSpace(submissionRole))
        {
            return "Filename must include driver/drv or merch/merchandiser.";
        }

        return string.Empty;
    }

    protected static string FormatFileSize(long sizeInBytes)
    {
        if (sizeInBytes < 1024)
        {
            return $"{sizeInBytes} B";
        }

        if (sizeInBytes < 1024 * 1024)
        {
            return $"{sizeInBytes / 1024d:N1} KB";
        }

        return $"{sizeInBytes / (1024d * 1024d):N1} MB";
    }
}