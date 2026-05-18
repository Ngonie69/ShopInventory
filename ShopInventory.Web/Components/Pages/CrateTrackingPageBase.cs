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
    protected bool isSubmittingOpeningBalance;

    protected int? selectedPodTransactionId;
    protected string selectedPodRole = "Driver";
    protected decimal podQuantity;
    protected string? podNotes;
    protected IBrowserFile? podFile;
    protected int podFileKey;
    protected bool isSubmittingPod;

    protected int? selectedGrvTransactionId;
    protected string grvReason = string.Empty;
    protected IBrowserFile? grvFile;
    protected int grvFileKey;
    protected bool isSubmittingGrv;

    protected int PendingPodCount => transactions.Count(t =>
        string.Equals(t.Status, "PendingDriverPod", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Status, "PendingMerchandiserPod", StringComparison.OrdinalIgnoreCase));

    protected int VariancePendingCount => transactions.Count(t =>
        string.Equals(t.Status, "VariancePendingGrv", StringComparison.OrdinalIgnoreCase));

    protected IEnumerable<CrateTransactionDto> PodEligibleTransactions => transactions
        .Where(t => string.Equals(t.TransactionType, "Invoice", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(t => t.EffectiveDate)
        .ThenByDescending(t => t.InvoiceDocNum);

    protected IEnumerable<CrateTransactionDto> GrvEligibleTransactions => transactions
        .Where(t => string.Equals(t.Status, "VariancePendingGrv", StringComparison.OrdinalIgnoreCase))
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
            SetStatus(false, "You do not have permission to upload opening balances.");
            return;
        }

        if (!await EnsureOpeningBalanceShopSelectedAsync())
        {
            SetStatus(false, "Select a shop from SAP before uploading the opening balance.");
            return;
        }

        if (openingBalanceQuantity <= 0)
        {
            SetStatus(false, "Opening balance quantity must be greater than zero.");
            return;
        }

        if (openingBalanceFile is null)
        {
            SetStatus(false, "A supporting document is required for the opening balance.");
            return;
        }

        isSubmittingOpeningBalance = true;

        try
        {
            var (success, message, _) = await CrateTrackingService.CreateOpeningBalanceAsync(
                openingBalanceShopCardCode.Trim(),
                openingBalanceQuantity,
                openingBalanceEffectiveDate,
                openingBalanceFile,
                openingBalanceNotes);

            SetStatus(success, message);
            if (success)
            {
                ResetOpeningBalanceForm();
                await RefreshAllAsync();
            }
        }
        finally
        {
            isSubmittingOpeningBalance = false;
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

        return $"{reference} | {shop} | Expected {transaction.ExpectedQuantity:N2}";
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
        openingBalanceFileKey++;
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