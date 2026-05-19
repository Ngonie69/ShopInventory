using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public abstract class CrateTrackingPageBase : ComponentBase
{
    [Inject] protected ICrateTrackingService CrateTrackingService { get; set; } = default!;
    [Inject] protected IBusinessPartnerService BusinessPartnerService { get; set; } = default!;
    [Inject] protected AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;
    [Inject] protected ILogger<CrateTrackingPageBase> Logger { get; set; } = default!;

    protected readonly List<CrateTransactionDto> transactions = [];
    protected readonly List<CratePodSubmissionDto> pods = [];
    protected readonly List<CrateGrvDto> grvs = [];

    protected bool isLoading;
    protected bool statusIsSuccess;
    protected string? statusMessage;

    protected bool canManageOpeningBalances;
    protected bool canRaiseGrvs;
    protected bool canChoosePodRole;

    protected string? transactionSearch;
    protected string? transactionStatusFilter;

    protected BusinessPartnerDto? selectedOpeningBalanceShop;
    protected string openingBalanceShopSearchTerm = string.Empty;
    protected readonly List<BusinessPartnerDto> openingBalanceShopResults = [];
    protected bool showOpeningBalanceShopResults;
    protected bool isOpeningBalanceShopLoading;

    protected string openingBalanceShopCardCode = string.Empty;
    protected decimal openingBalanceQuantity;
    protected DateTime openingBalanceEffectiveDate = DateTime.Today;
    protected string? openingBalanceNotes;
    protected IBrowserFile? openingBalanceFile;
    protected int openingBalanceFileKey;
    protected int? editingOpeningBalanceId;
    protected bool isSubmittingOpeningBalance;
    protected int? deletingOpeningBalanceId;

    protected int? selectedPodTransactionId;
    protected string selectedPodRole = "Driver";
    protected decimal podQuantity;
    protected string? podNotes;
    protected IBrowserFile? podFile;
    protected int podFileKey;
    protected bool isSubmittingPod;

    protected int? selectedGrvTransactionId;
    protected string grvReason = string.Empty;
    protected string selectedGrvReasonOption = string.Empty;
    protected IBrowserFile? grvFile;
    protected int grvFileKey;
    protected bool isSubmittingGrv;

    protected readonly List<string> grvReasonOptions =
    [
        "Customer retained empties from previous deliveries.",
        "Empty crates were not available for collection at the shop.",
        "Additional crates were supplied but not reflected on the invoice.",
        "Invoice crate quantity was captured incorrectly.",
        "Damaged crates were replaced during delivery.",
        "Crates were transferred between shops before merchandiser count.",
        "Physical count at the shop differed from the delivery handover."
    ];

    protected int PendingPodCount => transactions.Count(t =>
        string.Equals(t.Status, "PendingDriverPod", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Status, "PendingMerchandiserPod", StringComparison.OrdinalIgnoreCase));

    protected int VariancePendingCount => transactions.Count(CanRaiseGrv);

    protected IEnumerable<CrateTransactionDto> PodEligibleTransactions => transactions
        .Where(t => string.Equals(t.TransactionType, "Invoice", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(t => t.EffectiveDate)
        .ThenByDescending(t => t.InvoiceDocNum);

    protected IEnumerable<CrateTransactionDto> GrvEligibleTransactions => transactions
        .Where(CanRaiseGrv)
        .OrderByDescending(t => t.EffectiveDate)
        .ThenByDescending(t => t.InvoiceDocNum);

    protected override async Task OnInitializedAsync()
    {
        await LoadCurrentUserAsync();
        await RefreshAllAsync();
    }

    protected async Task LoadCurrentUserAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        canManageOpeningBalances = user.IsInRole("Admin");
        canRaiseGrvs = user.IsInRole("Admin") || user.IsInRole("Manager") || user.IsInRole("Merchandiser");
        canChoosePodRole = user.IsInRole("Admin") || user.IsInRole("Manager");

        selectedPodRole = user.IsInRole("Merchandiser")
            ? "Merchandiser"
            : "Driver";
    }

    protected async Task RefreshAllAsync()
    {
        isLoading = true;
        ClearStatus();

        try
        {
            var transactionTask = CrateTrackingService.GetTransactionsAsync(transactionSearch, transactionStatusFilter);
            var podTask = CrateTrackingService.GetPodsAsync();
            var grvTask = CrateTrackingService.GetGrvsAsync();

            await Task.WhenAll(transactionTask, podTask, grvTask);

            transactions.Clear();
            pods.Clear();
            grvs.Clear();

            transactions.AddRange((await transactionTask) ?? []);
            pods.AddRange((await podTask) ?? []);
            grvs.AddRange((await grvTask) ?? []);

            NormalizeSelections();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to refresh crate tracking page");
            SetStatus(false, "Failed to refresh crate tracking data.");
        }
        finally
        {
            isLoading = false;
        }
    }

    protected void NormalizeSelections()
    {
        if (selectedPodTransactionId.HasValue && !transactions.Any(t => t.Id == selectedPodTransactionId.Value))
        {
            selectedPodTransactionId = null;
        }

        if (selectedGrvTransactionId.HasValue && !transactions.Any(t => t.Id == selectedGrvTransactionId.Value))
        {
            selectedGrvTransactionId = null;
        }
    }

    protected void OnOpeningBalanceFileSelected(InputFileChangeEventArgs e)
    {
        openingBalanceFile = e.File;
        ClearStatus();
    }

    protected async Task OnOpeningBalanceShopSearchInputAsync(ChangeEventArgs e)
    {
        openingBalanceShopSearchTerm = e.Value?.ToString() ?? string.Empty;
        showOpeningBalanceShopResults = true;

        if (selectedOpeningBalanceShop is not null &&
            !string.Equals(selectedOpeningBalanceShop.DisplayName, openingBalanceShopSearchTerm, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(selectedOpeningBalanceShop.CardCode, openingBalanceShopSearchTerm, StringComparison.OrdinalIgnoreCase))
        {
            selectedOpeningBalanceShop = null;
            openingBalanceShopCardCode = string.Empty;
        }

        openingBalanceShopResults.Clear();
        var searchTerm = openingBalanceShopSearchTerm.Trim();

        if (searchTerm.Length < 2)
        {
            isOpeningBalanceShopLoading = false;
            await InvokeAsync(StateHasChanged);
            return;
        }

        _openingBalanceShopSearchCts?.Cancel();
        _openingBalanceShopSearchCts?.Dispose();
        _openingBalanceShopSearchCts = new CancellationTokenSource();
        var token = _openingBalanceShopSearchCts.Token;

        try
        {
            isOpeningBalanceShopLoading = true;
            await InvokeAsync(StateHasChanged);

            await Task.Delay(250, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var response = await BusinessPartnerService.SearchBusinessPartnersAsync(searchTerm);
            if (token.IsCancellationRequested)
            {
                return;
            }

            openingBalanceShopResults.AddRange((response?.BusinessPartners ?? [])
                .Where(IsCustomerBusinessPartner)
                .OrderBy(bp => bp.CardName ?? bp.CardCode)
                .Take(12));
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error searching SAP business partners for crate opening balance");
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                isOpeningBalanceShopLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    protected void ShowOpeningBalanceShopResults()
    {
        if (selectedOpeningBalanceShop is null && !string.IsNullOrWhiteSpace(openingBalanceShopSearchTerm))
        {
            showOpeningBalanceShopResults = true;
        }
    }

    protected async Task HandleOpeningBalanceShopBlurAsync()
    {
        await Task.Delay(200);
        showOpeningBalanceShopResults = false;
    }

    protected void SelectOpeningBalanceShop(BusinessPartnerDto businessPartner)
    {
        selectedOpeningBalanceShop = businessPartner;
        openingBalanceShopSearchTerm = businessPartner.DisplayName;
        openingBalanceShopCardCode = businessPartner.CardCode?.Trim() ?? string.Empty;
        openingBalanceShopResults.Clear();
        showOpeningBalanceShopResults = false;
        ClearStatus();
    }

    protected void ClearOpeningBalanceShopSelection()
    {
        selectedOpeningBalanceShop = null;
        openingBalanceShopSearchTerm = string.Empty;
        openingBalanceShopCardCode = string.Empty;
        openingBalanceShopResults.Clear();
        showOpeningBalanceShopResults = false;
    }

    protected void OnPodFileSelected(InputFileChangeEventArgs e)
    {
        podFile = e.File;
        ClearStatus();
    }

    protected void OnGrvFileSelected(InputFileChangeEventArgs e)
    {
        grvFile = e.File;
        ClearStatus();
    }

    protected async Task SubmitOpeningBalanceAsync()
    {
        ClearStatus();

        if (!canManageOpeningBalances)
        {
            SetStatus(false, "You do not have permission to manage opening balances.");
            return;
        }

        if (!await EnsureOpeningBalanceShopSelectedAsync())
        {
            SetStatus(false, "Select a shop from SAP before saving the opening balance.");
            return;
        }

        if (openingBalanceQuantity <= 0)
        {
            SetStatus(false, "Opening balance quantity must be greater than zero.");
            return;
        }

        isSubmittingOpeningBalance = true;

        try
        {
            var result = editingOpeningBalanceId.HasValue
                ? await CrateTrackingService.UpdateOpeningBalanceAsync(
                    editingOpeningBalanceId.Value,
                    openingBalanceShopCardCode.Trim(),
                    openingBalanceQuantity,
                    openingBalanceEffectiveDate,
                    openingBalanceFile,
                    openingBalanceNotes)
                : await CrateTrackingService.CreateOpeningBalanceAsync(
                    openingBalanceShopCardCode.Trim(),
                    openingBalanceQuantity,
                    openingBalanceEffectiveDate,
                    openingBalanceFile,
                    openingBalanceNotes);

            SetStatus(result.Success, result.Message);
            if (result.Success)
            {
                ResetOpeningBalanceForm();

                if (result.Transaction is not null)
                {
                    UpsertTransaction(result.Transaction);
                }
                else
                {
                    await RefreshAllAsync();
                }
            }
        }
        finally
        {
            isSubmittingOpeningBalance = false;
        }
    }

    protected void BeginOpeningBalanceEdit(CrateTransactionDto transaction)
    {
        ClearStatus();

        if (!canManageOpeningBalances || !IsOpeningBalance(transaction))
        {
            return;
        }

        editingOpeningBalanceId = transaction.Id;
        selectedOpeningBalanceShop = new BusinessPartnerDto
        {
            CardCode = transaction.ShopCardCode,
            CardName = transaction.ShopName,
            CardType = "cCustomer"
        };
        openingBalanceShopSearchTerm = string.IsNullOrWhiteSpace(transaction.ShopName)
            ? transaction.ShopCardCode
            : $"{transaction.ShopCardCode} - {transaction.ShopName}";
        openingBalanceShopCardCode = transaction.ShopCardCode;
        openingBalanceShopResults.Clear();
        showOpeningBalanceShopResults = false;
        isOpeningBalanceShopLoading = false;
        openingBalanceQuantity = transaction.ExpectedQuantity;
        openingBalanceEffectiveDate = transaction.EffectiveDate.Date;
        openingBalanceNotes = transaction.Notes;
        openingBalanceFile = null;
        openingBalanceFileKey++;
    }

    protected void CancelOpeningBalanceEdit()
    {
        ClearStatus();
        ResetOpeningBalanceForm();
    }

    protected async Task DeleteOpeningBalanceAsync(CrateTransactionDto transaction)
    {
        ClearStatus();

        if (!canManageOpeningBalances)
        {
            SetStatus(false, "You do not have permission to delete opening balances.");
            return;
        }

        if (!string.Equals(transaction.TransactionType, "OpeningBalance", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(false, "Only opening balances can be deleted from this register.");
            return;
        }

        var confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"Delete opening balance #{transaction.Id} for {transaction.ShopCardCode}? This cannot be undone.");

        if (!confirmed)
        {
            return;
        }

        deletingOpeningBalanceId = transaction.Id;

        try
        {
            var (success, message) = await CrateTrackingService.DeleteOpeningBalanceAsync(transaction.Id);
            SetStatus(success, message);

            if (success)
            {
                if (editingOpeningBalanceId == transaction.Id)
                {
                    ResetOpeningBalanceForm();
                }

                RemoveTransaction(transaction.Id);
            }
        }
        finally
        {
            deletingOpeningBalanceId = null;
        }
    }

    protected async Task SubmitPodAsync()
    {
        ClearStatus();

        if (!selectedPodTransactionId.HasValue)
        {
            SetStatus(false, "Select a crate transaction first.");
            return;
        }

        if (podQuantity < 0)
        {
            SetStatus(false, "Crate POD quantity cannot be negative.");
            return;
        }

        if (podFile is null)
        {
            SetStatus(false, "Attach the POD document before uploading.");
            return;
        }

        isSubmittingPod = true;

        try
        {
            var role = canChoosePodRole ? selectedPodRole : null;
            var (success, message, _) = await CrateTrackingService.UploadCratePodAsync(
                selectedPodTransactionId.Value,
                podQuantity,
                podFile,
                role,
                podNotes);

            SetStatus(success, message);
            if (success)
            {
                ResetPodForm();
                await RefreshAllAsync();
            }
        }
        finally
        {
            isSubmittingPod = false;
        }
    }

    protected async Task SubmitGrvAsync()
    {
        ClearStatus();

        if (!canRaiseGrvs)
        {
            SetStatus(false, "You do not have permission to raise crate GRVs.");
            return;
        }

        if (!selectedGrvTransactionId.HasValue)
        {
            SetStatus(false, "Select the transaction that needs a GRV.");
            return;
        }

        if (string.IsNullOrWhiteSpace(grvReason))
        {
            SetStatus(false, "A GRV reason is required.");
            return;
        }

        if (grvFile is null)
        {
            SetStatus(false, "Attach the GRV document before creating the GRV.");
            return;
        }

        isSubmittingGrv = true;

        try
        {
            var (success, message, _) = await CrateTrackingService.CreateCrateGrvAsync(
                selectedGrvTransactionId.Value,
                grvReason.Trim(),
                grvFile);

            SetStatus(success, message);
            if (success)
            {
                ResetGrvForm();
                await RefreshAllAsync();
            }
        }
        finally
        {
            isSubmittingGrv = false;
        }
    }

    protected void ApplyGrvReasonOption(ChangeEventArgs e)
    {
        selectedGrvReasonOption = e.Value?.ToString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(selectedGrvReasonOption))
        {
            grvReason = selectedGrvReasonOption;
        }

        ClearStatus();
    }

    protected async Task DownloadAttachmentAsync(DocumentAttachmentDto attachment)
    {
        try
        {
            await JS.InvokeVoidAsync("downloadAuthenticatedFile", attachment.DownloadUrl, attachment.FileName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to download crate attachment {AttachmentId}", attachment.Id);
            SetStatus(false, "The document could not be downloaded.");
        }
    }

    protected static string GetReferenceLabel(CrateTransactionDto transaction)
    {
        return transaction.TransactionType == "OpeningBalance"
            ? $"Opening Balance #{transaction.Id}"
            : $"Invoice #{transaction.InvoiceDocNum}";
    }

    protected static string GetTransactionLabel(CrateTransactionDto transaction)
    {
        var reference = transaction.TransactionType == "OpeningBalance"
            ? $"Opening Balance #{transaction.Id}"
            : $"Invoice #{transaction.InvoiceDocNum}";

        var shop = string.IsNullOrWhiteSpace(transaction.ShopName)
            ? transaction.ShopCardCode
            : $"{transaction.ShopCardCode} - {transaction.ShopName}";

        var quantityLabel = string.Equals(transaction.TransactionType, "OpeningBalance", StringComparison.OrdinalIgnoreCase)
            ? "Actual"
            : "Expected";

        return $"{reference} | {shop} | {quantityLabel} {transaction.ExpectedQuantity:N2}";
    }

    protected static string GetPodReferenceLabel(CratePodSubmissionDto pod)
    {
        return pod.InvoiceDocNum.HasValue ? $"Invoice #{pod.InvoiceDocNum}" : $"Transaction #{pod.CrateTransactionId}";
    }

    protected static string GetGrvReferenceLabel(CrateGrvDto grv)
    {
        return grv.InvoiceDocNum.HasValue ? $"Invoice #{grv.InvoiceDocNum}" : $"Transaction #{grv.CrateTransactionId}";
    }

    private CancellationTokenSource? _openingBalanceShopSearchCts;

    private async Task<bool> EnsureOpeningBalanceShopSelectedAsync()
    {
        if (selectedOpeningBalanceShop is not null && !string.IsNullOrWhiteSpace(openingBalanceShopCardCode))
        {
            return true;
        }

        var searchTerm = openingBalanceShopSearchTerm.Trim();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return false;
        }

        try
        {
            var exactCodeMatch = await BusinessPartnerService.GetBusinessPartnerByCodeAsync(searchTerm);
            if (IsCustomerBusinessPartner(exactCodeMatch))
            {
                SelectOpeningBalanceShop(exactCodeMatch!);
                return true;
            }

            var response = await BusinessPartnerService.SearchBusinessPartnersAsync(searchTerm);
            var exactMatch = (response?.BusinessPartners ?? [])
                .Where(IsCustomerBusinessPartner)
                .FirstOrDefault(bp =>
                    string.Equals(bp.CardCode, searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bp.CardName, searchTerm, StringComparison.OrdinalIgnoreCase));

            if (exactMatch is null)
            {
                return false;
            }

            SelectOpeningBalanceShop(exactMatch);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error resolving SAP business partner for crate opening balance");
            return false;
        }
    }

    private static bool IsCustomerBusinessPartner(BusinessPartnerDto? businessPartner)
    {
        if (businessPartner is null || string.IsNullOrWhiteSpace(businessPartner.CardCode))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(businessPartner.CardType)
            || string.Equals(businessPartner.CardType, "cCustomer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(businessPartner.CardType, "C", StringComparison.OrdinalIgnoreCase);
    }

    protected static string FormatQuantity(decimal? quantity)
    {
        return quantity.HasValue ? quantity.Value.ToString("N2") : "-";
    }

    protected static string FormatVariance(decimal? variance)
    {
        if (!variance.HasValue)
        {
            return "-";
        }

        return variance.Value.ToString("+0.##;-0.##;0");
    }

    protected static bool IsOpeningBalance(CrateTransactionDto transaction)
    {
        return string.Equals(transaction.TransactionType, "OpeningBalance", StringComparison.OrdinalIgnoreCase);
    }

    protected static bool CanRaiseGrv(CrateTransactionDto transaction)
    {
        return string.Equals(transaction.TransactionType, "Invoice", StringComparison.OrdinalIgnoreCase)
            && !transaction.HasGrv
            && transaction.MerchandiserQuantity.HasValue
            && transaction.MerchandiserQuantity.Value != transaction.ExpectedQuantity;
    }

    protected static string GetRegisterQuantityLabel(CrateTransactionDto transaction)
    {
        return IsOpeningBalance(transaction) ? "Actual counted" : "Expected";
    }

    protected bool IsEditingOpeningBalance(CrateTransactionDto transaction)
    {
        return editingOpeningBalanceId == transaction.Id;
    }

    protected static string GetStatusClass(string status)
    {
        return status switch
        {
            "Matched" => "crt-badge-success",
            "GrvRaised" => "crt-badge-danger",
            "VariancePendingGrv" => "crt-badge-danger",
            _ => "crt-badge-pending"
        };
    }

    protected static string GetStatusLabel(string status)
    {
        return status switch
        {
            "PendingDriverPod" => "Pending Driver POD",
            "PendingMerchandiserPod" => "Pending Merch POD",
            "VariancePendingGrv" => "Variance Pending GRV",
            "GrvRaised" => "GRV Raised",
            _ => "Matched"
        };
    }

    protected void ResetOpeningBalanceForm()
    {
        _openingBalanceShopSearchCts?.Cancel();
        _openingBalanceShopSearchCts?.Dispose();
        _openingBalanceShopSearchCts = null;
        selectedOpeningBalanceShop = null;
        openingBalanceShopSearchTerm = string.Empty;
        openingBalanceShopResults.Clear();
        showOpeningBalanceShopResults = false;
        isOpeningBalanceShopLoading = false;
        openingBalanceShopCardCode = string.Empty;
        openingBalanceQuantity = 0;
        openingBalanceEffectiveDate = DateTime.Today;
        openingBalanceNotes = null;
        openingBalanceFile = null;
        editingOpeningBalanceId = null;
        openingBalanceFileKey++;
    }

    private void UpsertTransaction(CrateTransactionDto transaction)
    {
        var existingIndex = transactions.FindIndex(item => item.Id == transaction.Id);

        if (!MatchesCurrentTransactionFilters(transaction))
        {
            if (existingIndex >= 0)
            {
                transactions.RemoveAt(existingIndex);
            }

            NormalizeSelections();
            return;
        }

        if (existingIndex >= 0)
        {
            transactions[existingIndex] = transaction;
        }
        else
        {
            transactions.Add(transaction);
        }

        SortTransactions();
        NormalizeSelections();
    }

    private void RemoveTransaction(int transactionId)
    {
        var existingIndex = transactions.FindIndex(item => item.Id == transactionId);
        if (existingIndex >= 0)
        {
            transactions.RemoveAt(existingIndex);
        }

        NormalizeSelections();
    }

    private bool MatchesCurrentTransactionFilters(CrateTransactionDto transaction)
    {
        if (!string.IsNullOrWhiteSpace(transactionStatusFilter) &&
            !string.Equals(transaction.Status, transactionStatusFilter.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(transactionSearch))
        {
            return true;
        }

        var searchTerm = transactionSearch.Trim();
        if (searchTerm.Length == 0)
        {
            return true;
        }

        return ContainsIgnoreCase(transaction.ShopCardCode, searchTerm)
            || ContainsIgnoreCase(transaction.ShopName, searchTerm)
            || (transaction.InvoiceDocNum?.ToString()?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void SortTransactions()
    {
        transactions.Sort(static (left, right) =>
        {
            var effectiveDateComparison = right.EffectiveDate.CompareTo(left.EffectiveDate);
            if (effectiveDateComparison != 0)
            {
                return effectiveDateComparison;
            }

            var createdAtComparison = right.CreatedAt.CompareTo(left.CreatedAt);
            if (createdAtComparison != 0)
            {
                return createdAtComparison;
            }

            return right.Id.CompareTo(left.Id);
        });
    }

    private static bool ContainsIgnoreCase(string? value, string searchTerm)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);
    }

    protected void ResetPodForm()
    {
        selectedPodTransactionId = null;
        podQuantity = 0;
        podNotes = null;
        podFile = null;
        podFileKey++;
    }

    protected void ResetGrvForm()
    {
        selectedGrvTransactionId = null;
        selectedGrvReasonOption = string.Empty;
        grvReason = string.Empty;
        grvFile = null;
        grvFileKey++;
    }

    protected void SetStatus(bool success, string message)
    {
        statusIsSuccess = success;
        statusMessage = message;
    }

    protected void ClearStatus()
    {
        statusMessage = null;
        statusIsSuccess = false;
    }
}