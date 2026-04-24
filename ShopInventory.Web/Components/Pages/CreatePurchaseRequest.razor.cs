using MediatR;
using Microsoft.AspNetCore.Components;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.PurchaseRequests.Commands.CreatePurchaseRequest;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class CreatePurchaseRequest
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<CreatePurchaseRequest> Logger { get; set; } = default!;

    private readonly Dictionary<string, string> validationErrors = new();
    private CreatePurchaseRequestRequest purchaseRequest = CreateDefaultRequest();
    private List<ProductDto> products = new();
    private List<WarehouseDto> warehouses = new();
    private string? errorMessage;
    private string? successMessage;
    private bool isLoadingLookups = true;
    private bool isSubmitting;
    private bool hasInitialized;

    private decimal TotalQuantity => purchaseRequest.Lines.Sum(line => line.Quantity);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadLookupsAsync();
        StateHasChanged();
    }

    private static CreatePurchaseRequestRequest CreateDefaultRequest() => new()
    {
        DocDate = DateTime.Today,
        RequriedDate = DateTime.Today,
        Lines = new List<CreatePurchaseRequestLineRequest>
        {
            new() { RequiredDate = DateTime.Today }
        }
    };

    private async Task LoadLookupsAsync()
    {
        errorMessage = null;
        isLoadingLookups = true;

        try
        {
            var productTask = CacheService.GetProductsAsync();
            var warehouseTask = CacheService.GetWarehousesAsync();

            await Task.WhenAll(productTask, warehouseTask);

            products = await productTask;
            warehouses = await warehouseTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load purchase request lookups");
            errorMessage = "Failed to load products or warehouses. Exact codes can still be entered manually.";
        }
        finally
        {
            isLoadingLookups = false;
        }
    }

    private void AddLine()
    {
        purchaseRequest.Lines.Add(new CreatePurchaseRequestLineRequest
        {
            RequiredDate = purchaseRequest.RequriedDate
        });
    }

    private void RemoveLine(int index)
    {
        if (purchaseRequest.Lines.Count == 1)
            return;

        purchaseRequest.Lines.RemoveAt(index);
        ClearLineValidation(index);
    }

    private void SyncProduct(int index)
    {
        if (index < 0 || index >= purchaseRequest.Lines.Count)
            return;

        var line = purchaseRequest.Lines[index];
        if (string.IsNullOrWhiteSpace(line.ItemCode))
            return;

        var product = products.FirstOrDefault(item =>
            string.Equals(item.ItemCode, line.ItemCode, StringComparison.OrdinalIgnoreCase));

        if (product is null)
            return;

        if (string.IsNullOrWhiteSpace(line.ItemDescription))
        {
            line.ItemDescription = product.ItemName;
        }

        if (string.IsNullOrWhiteSpace(line.WarehouseCode))
        {
            line.WarehouseCode = product.DefaultWarehouse;
        }

        if (string.IsNullOrWhiteSpace(line.UoMCode))
        {
            line.UoMCode = product.UoM;
        }

        line.RequiredDate ??= purchaseRequest.RequriedDate;
    }

    private bool HasError(string key) => validationErrors.ContainsKey(key);
    private string GetError(string key) => validationErrors.TryGetValue(key, out var value) ? value : string.Empty;

    private void ClearLineValidation(int removedIndex)
    {
        var keysToRemove = validationErrors.Keys
            .Where(key => key.StartsWith($"Lines[{removedIndex}]", StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            validationErrors.Remove(key);
        }
    }

    private bool Validate()
    {
        validationErrors.Clear();
        errorMessage = null;

        if (purchaseRequest.Lines.Count == 0)
        {
            validationErrors["Lines"] = "At least one request line is required.";
        }

        for (var index = 0; index < purchaseRequest.Lines.Count; index++)
        {
            var line = purchaseRequest.Lines[index];
            if (string.IsNullOrWhiteSpace(line.ItemCode))
                validationErrors[$"Lines[{index}].ItemCode"] = "Item code is required.";
            if (line.Quantity <= 0)
                validationErrors[$"Lines[{index}].Quantity"] = "Quantity must be greater than zero.";
        }

        return validationErrors.Count == 0;
    }

    private async Task SubmitAsync()
    {
        if (!Validate())
            return;

        isSubmitting = true;
        errorMessage = null;
        successMessage = null;

        try
        {
            foreach (var line in purchaseRequest.Lines)
            {
                line.RequiredDate ??= purchaseRequest.RequriedDate;
            }

            PurchaseRequestDto? createdRequest = null;
            var result = await Mediator.Send(new CreatePurchaseRequestCommand(purchaseRequest));

            result.SwitchFirst(
                value => createdRequest = value,
                error => errorMessage = error.Description);

            if (createdRequest is null)
            {
                await AuditService.LogAsync(
                    AuditActions.CreatePurchaseRequest,
                    "PurchaseRequest",
                    null,
                    $"{purchaseRequest.Lines.Count} line(s)",
                    false,
                    errorMessage);
                return;
            }

            successMessage = $"Purchase request #{createdRequest.DocNum} created successfully.";

            await AuditService.LogAsync(
                AuditActions.CreatePurchaseRequest,
                "PurchaseRequest",
                createdRequest.DocEntry.ToString(),
                $"{createdRequest.Lines.Count} line(s)",
                true);

            await Task.Delay(900);
            NavigationManager.NavigateTo("/purchase-requests");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected failure while posting purchase request");
            errorMessage = "An unexpected error occurred while creating the purchase request.";

            await AuditService.LogAsync(
                AuditActions.CreatePurchaseRequest,
                "PurchaseRequest",
                null,
                $"{purchaseRequest.Lines.Count} line(s)",
                false,
                ex.Message);
        }
        finally
        {
            isSubmitting = false;
        }
    }

    private void GoBack()
    {
        NavigationManager.NavigateTo("/purchase-requests");
    }
}