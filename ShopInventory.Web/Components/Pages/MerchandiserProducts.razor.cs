using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor;
using ShopInventory.Web.Features.Merchandiser.Commands.BackfillProductDetails;
using ShopInventory.Web.Features.Merchandiser.Queries.GetGlobalProducts;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class MerchandiserProducts
{
    [Inject] private IMerchandiserService MerchandiserService { get; set; } = default!;
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    private MerchandiserProductListResponse? productResponse;

    private bool isLoading = true;
    private bool isLoadingProducts;
    private bool isSaving;
    private bool isBackfilling;
    private string productSearchTerm = string.Empty;
    private string statusFilter = "active";
    private string categoryFilter = string.Empty;
    private readonly HashSet<string> selectedProductCodes = new();

    private bool showAssignModal;
    private bool isLoadingAssignProducts;
    private string assignSearchTerm = string.Empty;
    private readonly HashSet<string> assignSelectedCodes = new();
    private List<SapSalesItemDto> allSalesItems = [];
    private string assignLoadError = string.Empty;

    private bool hasLoadedData;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasLoadedData)
            return;

        hasLoadedData = true;
        await AuthStateProvider.GetAuthenticationStateAsync();
        await LoadGlobalProducts();
        isLoading = false;
        StateHasChanged();
    }

    private async Task LoadGlobalProducts()
    {
        isLoadingProducts = true;

        try
        {
            var result = await Mediator.Send(new GetGlobalProductsQuery());
            result.SwitchFirst(
                value => productResponse = value,
                error => Snackbar.Add(error.Description, Severity.Error));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error loading products: {ex.Message}", Severity.Error);
        }
        finally
        {
            isLoadingProducts = false;
        }
    }

    private IEnumerable<string> AvailableCategories
    {
        get
        {
            if (productResponse == null)
                return Enumerable.Empty<string>();

            return productResponse.Products
                .Where(product => !string.IsNullOrEmpty(product.Category))
                .Select(product => product.Category!)
                .Distinct()
                .OrderBy(category => category);
        }
    }

    private IEnumerable<MerchandiserProductDto> FilteredProducts
    {
        get
        {
            if (productResponse == null)
                return Enumerable.Empty<MerchandiserProductDto>();

            var products = productResponse.Products.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(productSearchTerm))
            {
                products = products.Where(product =>
                    (product.ItemCode?.Contains(productSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (product.ItemName?.Contains(productSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(categoryFilter))
                products = products.Where(product => product.Category == categoryFilter);

            if (statusFilter == "active")
                products = products.Where(product => product.IsActive);
            else if (statusFilter == "inactive")
                products = products.Where(product => !product.IsActive);

            return products;
        }
    }

    private void ToggleProductSelection(string itemCode, bool isChecked)
    {
        if (isChecked)
            selectedProductCodes.Add(itemCode);
        else
            selectedProductCodes.Remove(itemCode);
    }

    private async Task ToggleSingleProduct(string itemCode, bool isActive)
    {
        isSaving = true;

        try
        {
            await MerchandiserService.UpdateProductStatusGlobalAsync([itemCode], isActive);
            Snackbar.Add($"{itemCode} {(isActive ? "activated" : "deactivated")}", Severity.Success);
            await LoadGlobalProducts();

            await AuditService.LogAsync(
                isActive ? "ActivateMerchandiserProduct" : "DeactivateMerchandiserProduct",
                "MerchandiserProduct",
                $"Global: {itemCode}");
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task RemoveSingleProduct(string itemCode)
    {
        isSaving = true;

        try
        {
            await MerchandiserService.RemoveProductsGlobalAsync([itemCode]);
            productResponse?.Products.RemoveAll(product => product.ItemCode == itemCode);
            RecalculateCounts();
            selectedProductCodes.Remove(itemCode);
            Snackbar.Add($"{itemCode} removed", Severity.Success);

            await AuditService.LogAsync("RemoveMerchandiserProduct", "MerchandiserProduct", $"Global: {itemCode}");
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task RemoveSelected()
    {
        if (selectedProductCodes.Count == 0)
            return;

        isSaving = true;

        try
        {
            var codesToRemove = selectedProductCodes.ToList();
            await MerchandiserService.RemoveProductsGlobalAsync(codesToRemove);
            productResponse?.Products.RemoveAll(product => codesToRemove.Contains(product.ItemCode));
            RecalculateCounts();
            Snackbar.Add($"{codesToRemove.Count} products removed", Severity.Success);
            await AuditService.LogAsync(
                "RemoveMerchandiserProducts",
                "MerchandiserProduct",
                $"Global: {codesToRemove.Count} products");
            selectedProductCodes.Clear();
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task ActivateSelected()
    {
        if (selectedProductCodes.Count == 0)
            return;

        isSaving = true;

        try
        {
            var activateCodes = selectedProductCodes.ToList();
            await MerchandiserService.UpdateProductStatusGlobalAsync(activateCodes, true);
            Snackbar.Add($"{activateCodes.Count} products activated", Severity.Success);
            selectedProductCodes.Clear();
            await LoadGlobalProducts();
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task DeactivateSelected()
    {
        if (selectedProductCodes.Count == 0)
            return;

        isSaving = true;

        try
        {
            var deactivateCodes = selectedProductCodes.ToList();
            await MerchandiserService.UpdateProductStatusGlobalAsync(deactivateCodes, false);
            Snackbar.Add($"{deactivateCodes.Count} products deactivated", Severity.Success);
            selectedProductCodes.Clear();
            await LoadGlobalProducts();
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task ShowAssignModal()
    {
        assignSearchTerm = string.Empty;
        assignSelectedCodes.Clear();
        assignLoadError = string.Empty;
        isLoadingAssignProducts = true;
        showAssignModal = true;
        StateHasChanged();

        try
        {
            allSalesItems = await MerchandiserService.GetSapSalesItemsAsync();

            if (allSalesItems.Count == 0)
                assignLoadError = "No sales items found in SAP.";
        }
        catch (Exception ex)
        {
            allSalesItems = [];
            assignLoadError = $"Failed to load sales items from SAP: {ex.Message}";
        }
        finally
        {
            isLoadingAssignProducts = false;
        }
    }

    private void CloseAssignModal()
    {
        showAssignModal = false;
    }

    private IEnumerable<SapSalesItemDto> FilteredAssignableProducts
    {
        get
        {
            var assignedCodes = productResponse?.Products.Select(product => product.ItemCode).ToHashSet() ?? [];

            var products = allSalesItems
                .Where(product => !assignedCodes.Contains(product.ItemCode));

            if (!string.IsNullOrWhiteSpace(assignSearchTerm))
            {
                products = products.Where(product =>
                    product.ItemCode.Contains(assignSearchTerm, StringComparison.OrdinalIgnoreCase) ||
                    product.ItemName.Contains(assignSearchTerm, StringComparison.OrdinalIgnoreCase));
            }

            return products;
        }
    }

    private void ToggleAssignProduct(string itemCode, bool isChecked)
    {
        if (isChecked)
            assignSelectedCodes.Add(itemCode);
        else
            assignSelectedCodes.Remove(itemCode);
    }

    private void ToggleSelectAllAssign(ChangeEventArgs e)
    {
        if ((bool)(e.Value ?? false))
        {
            foreach (var product in FilteredAssignableProducts)
            {
                assignSelectedCodes.Add(product.ItemCode);
            }
        }
        else
        {
            assignSelectedCodes.Clear();
        }
    }

    private async Task AssignSelectedProducts()
    {
        if (assignSelectedCodes.Count == 0)
            return;

        isSaving = true;

        try
        {
            var itemNames = allSalesItems
                .Where(product => assignSelectedCodes.Contains(product.ItemCode))
                .ToDictionary(product => product.ItemCode, product => product.ItemName);

            await MerchandiserService.AssignProductsGlobalAsync(assignSelectedCodes.ToList(), itemNames);
            Snackbar.Add($"{assignSelectedCodes.Count} products assigned to all merchandisers", Severity.Success);
            CloseAssignModal();
            await LoadGlobalProducts();

            await AuditService.LogAsync(
                "AssignMerchandiserProducts",
                "MerchandiserProduct",
                $"Global: {assignSelectedCodes.Count} products");
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, Severity.Error);
        }
        finally
        {
            isSaving = false;
        }
    }

    private void RecalculateCounts()
    {
        if (productResponse == null)
            return;

        productResponse.TotalCount = productResponse.Products.Count;
        productResponse.ActiveCount = productResponse.Products.Count(product => product.IsActive);
    }

    private async Task BackfillProductDetails()
    {
        isBackfilling = true;
        StateHasChanged();

        try
        {
            var result = await Mediator.Send(new BackfillProductDetailsCommand());
            result.SwitchFirst(
                value => Snackbar.Add($"Synced product details for {value} records from SAP", Severity.Success),
                error => Snackbar.Add(error.Description, Severity.Error));

            if (!result.IsError)
                await LoadGlobalProducts();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to sync product details: {ex.Message}", Severity.Error);
        }
        finally
        {
            isBackfilling = false;
            StateHasChanged();
        }
    }
}