using Blazored.LocalStorage;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.GoodsReceiptPurchaseOrders.Commands.CreateGoodsReceiptPurchaseOrder;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components.Pages;

public partial class CreateGoodsReceiptPurchaseOrder
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private IMasterDataCacheService CacheService { get; set; } = default!;
    [Inject] private IPurchaseOrderService PurchaseOrderService { get; set; } = default!;
    [Inject] private IAuditService AuditService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILogger<CreateGoodsReceiptPurchaseOrder> Logger { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthStateProvider { get; set; } = default!;
    [Inject] private ILocalStorageService LocalStorage { get; set; } = default!;

    // Draft persistence: the form is written to the browser after each change, so a reload after
    // the circuit is lost does not lose the receipt. See FormDraft.
    private const string DraftForm = "goods-receipt-po";
    private FormDraftStore<CreateGoodsReceiptPurchaseOrderRequest>? draftStore;
    private string? draftNotice;

    // A receipt started from a purchase order (?purchaseOrderDocEntry=) keeps its own draft, so it
    // never restores over a different order's prefill.
    private string DraftFormKey()
    {
        var query = QueryHelpers.ParseQuery(NavigationManager.ToAbsoluteUri(NavigationManager.Uri).Query);
        return query.TryGetValue("purchaseOrderDocEntry", out var docEntry)
            && int.TryParse(docEntry.ToString(), out var entry) && entry > 0
                ? $"{DraftForm}-po-{entry}"
                : DraftForm;
    }

    private readonly Dictionary<string, string> validationErrors = new();
    private CreateGoodsReceiptPurchaseOrderRequest goodsReceipt = CreateDefaultRequest();
    private List<BusinessPartnerDto> suppliers = new();
    private List<ProductDto> products = new();
    private List<WarehouseDto> warehouses = new();
    private BusinessPartnerDto? selectedSupplier;
    private string supplierSearchTerm = string.Empty;
    private string? errorMessage;
    private string? successMessage;
    private bool isLoadingLookups = true;
    private bool isSubmitting;
    private bool showSupplierResults;
    private bool hasInitialized;

    private IEnumerable<BusinessPartnerDto> FilteredSuppliers => suppliers
        .Where(supplier =>
            !string.IsNullOrWhiteSpace(supplier.CardCode) &&
            (string.IsNullOrWhiteSpace(supplierSearchTerm) ||
             supplier.CardCode.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ||
             (supplier.CardName?.Contains(supplierSearchTerm, StringComparison.OrdinalIgnoreCase) ?? false)))
        .Take(12)
        .ToList();

    private decimal SubTotal => goodsReceipt.Lines.Sum(CalculateLineTotal);
    private decimal HeaderDiscountAmount => SubTotal * (goodsReceipt.DiscountPercent / 100m);
    private decimal EstimatedTotal => Math.Max(0, SubTotal - HeaderDiscountAmount);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender && draftStore is not null)
        {
            await draftStore.SaveAsync(goodsReceipt, IsDraftEmpty(goodsReceipt));
        }

        if (!firstRender || hasInitialized)
            return;

        hasInitialized = true;
        await LoadLookupsAsync();
        await PrefillFromPurchaseOrderAsync();

        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var store = new FormDraftStore<CreateGoodsReceiptPurchaseOrderRequest>(LocalStorage, Logger, DraftFormKey(), authState.User);
        ApplyDraft(await store.RestoreAsync());
        store.Accept(goodsReceipt);
        draftStore = store;

        StateHasChanged();
    }

    // The form opens with one blank line, which is not something to bring back.
    private static bool IsDraftEmpty(CreateGoodsReceiptPurchaseOrderRequest receipt)
        => string.IsNullOrWhiteSpace(receipt.CardCode)
            && receipt.Lines.All(l => string.IsNullOrWhiteSpace(l.ItemCode) && l.Quantity == 0)
            && string.IsNullOrWhiteSpace(receipt.NumAtCard)
            && string.IsNullOrWhiteSpace(receipt.Comments);

    private void ApplyDraft(FormDraft.Restored<CreateGoodsReceiptPurchaseOrderRequest>? restored)
    {
        if (restored is null || IsDraftEmpty(restored.State))
        {
            return;
        }

        var draft = restored.State;
        draft.Lines ??= new List<CreateGoodsReceiptPurchaseOrderLineRequest>();
        if (draft.Lines.Count == 0)
        {
            draft.Lines.Add(new CreateGoodsReceiptPurchaseOrderLineRequest());
        }

        goodsReceipt = draft;

        var supplier = string.IsNullOrWhiteSpace(goodsReceipt.CardCode)
            ? null
            : suppliers.FirstOrDefault(s => string.Equals(s.CardCode, goodsReceipt.CardCode, StringComparison.OrdinalIgnoreCase));
        if (supplier is not null)
        {
            selectedSupplier = supplier;
            supplierSearchTerm = supplier.DisplayName;
        }
        else
        {
            // A code typed by hand is how a supplier outside the list is entered, so it stays.
            supplierSearchTerm = goodsReceipt.CardCode;
        }

        var lineCount = goodsReceipt.Lines.Count(l => !string.IsNullOrWhiteSpace(l.ItemCode));
        draftNotice = FormDraft.RestoredNotice("goods receipt", lineCount, restored.SavedAt);
        Logger.LogInformation("Restored a goods receipt draft with {LineCount} lines for {CardCode}", lineCount, draft.CardCode);
    }

    private void DiscardRestoredDraft()
    {
        draftNotice = null;
        selectedSupplier = null;
        supplierSearchTerm = string.Empty;
        goodsReceipt = CreateDefaultRequest();
        validationErrors.Clear();
    }

    private async Task CancelAsync()
    {
        // Cancel abandons the receipt, so its draft goes too. The nav keeps it.
        await ClearDraftAsync();
        GoBack();
    }

    private async Task ClearDraftAsync()
    {
        if (draftStore is not null)
        {
            var store = draftStore;
            draftStore = null;
            await store.ClearAsync();
        }
    }

    private static CreateGoodsReceiptPurchaseOrderRequest CreateDefaultRequest() => new()
    {
        DocDate = DateTime.Today,
        DocDueDate = DateTime.Today,
        DocCurrency = "USD",
        Lines = new List<CreateGoodsReceiptPurchaseOrderLineRequest>
        {
            new()
        }
    };

    private async Task LoadLookupsAsync()
    {
        errorMessage = null;
        isLoadingLookups = true;

        try
        {
            var supplierTask = CacheService.GetBusinessPartnersAsync();
            var productTask = CacheService.GetProductsAsync();
            var warehouseTask = CacheService.GetWarehousesAsync();

            await Task.WhenAll(supplierTask, productTask, warehouseTask);

            suppliers = await supplierTask;
            products = await productTask;
            warehouses = await warehouseTask;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load goods receipt PO lookups");
            errorMessage = "Failed to load suppliers, products, or warehouses. Exact codes can still be entered manually.";
        }
        finally
        {
            isLoadingLookups = false;
        }
    }

    private async Task PrefillFromPurchaseOrderAsync()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var query = QueryHelpers.ParseQuery(uri.Query);

        if (!query.TryGetValue("purchaseOrderDocEntry", out var docEntryValues) ||
            !int.TryParse(docEntryValues.ToString(), out var docEntry) ||
            docEntry <= 0)
        {
            return;
        }

        try
        {
            var purchaseOrder = await PurchaseOrderService.GetPurchaseOrderFromSAPByDocEntryAsync(docEntry);
            if (purchaseOrder is null)
            {
                errorMessage ??= "Failed to load the SAP purchase order for GRPO prefilling.";
                return;
            }

            ApplyPurchaseOrderPrefill(purchaseOrder, docEntry);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to prefill GRPO draft from SAP purchase order {DocEntry}", docEntry);
            errorMessage ??= "Failed to prefill the goods receipt draft from the selected purchase order.";
        }
    }

    private void ApplyPurchaseOrderPrefill(PurchaseOrderDto purchaseOrder, int docEntry)
    {
        goodsReceipt.CardCode = purchaseOrder.CardCode;
        goodsReceipt.DocCurrency = string.IsNullOrWhiteSpace(purchaseOrder.Currency) ? goodsReceipt.DocCurrency : purchaseOrder.Currency;
        goodsReceipt.DocDueDate = purchaseOrder.DeliveryDate ?? goodsReceipt.DocDueDate;
        goodsReceipt.NumAtCard = purchaseOrder.SupplierRefNo;
        goodsReceipt.DiscountPercent = purchaseOrder.DiscountPercent;

        if (string.IsNullOrWhiteSpace(goodsReceipt.Comments))
        {
            goodsReceipt.Comments = $"Receipt against PO {purchaseOrder.OrderNumber}";
        }

        selectedSupplier = suppliers.FirstOrDefault(supplier =>
            string.Equals(supplier.CardCode, purchaseOrder.CardCode, StringComparison.OrdinalIgnoreCase));
        supplierSearchTerm = selectedSupplier?.DisplayName ?? $"{purchaseOrder.CardCode} - {purchaseOrder.CardName}";
        showSupplierResults = false;

        var sourceLines = purchaseOrder.Lines
            .Where(line => line.QuantityRemaining > 0)
            .ToList();

        if (sourceLines.Count == 0)
        {
            sourceLines = purchaseOrder.Lines.ToList();
        }

        goodsReceipt.Lines = sourceLines
            .Select(line => new CreateGoodsReceiptPurchaseOrderLineRequest
            {
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.QuantityRemaining > 0 ? line.QuantityRemaining : line.Quantity,
                UnitPrice = line.UnitPrice,
                WarehouseCode = line.WarehouseCode ?? purchaseOrder.WarehouseCode,
                UoMCode = line.UoMCode,
                BaseEntry = docEntry,
                BaseLine = line.LineNum,
                BaseType = 22
            })
            .ToList();

        if (goodsReceipt.Lines.Count == 0)
        {
            goodsReceipt.Lines = new List<CreateGoodsReceiptPurchaseOrderLineRequest>
            {
                new()
            };
        }
    }

    private void ShowSupplierResults() => showSupplierResults = true;

    private async Task HandleSupplierBlur()
    {
        await Task.Delay(150);
        showSupplierResults = false;
    }

    private void OnSupplierInput(ChangeEventArgs args)
    {
        supplierSearchTerm = args.Value?.ToString() ?? string.Empty;
        if (selectedSupplier is not null && !string.Equals(selectedSupplier.DisplayName, supplierSearchTerm, StringComparison.OrdinalIgnoreCase))
        {
            selectedSupplier = null;
            goodsReceipt.CardCode = string.Empty;
        }
        showSupplierResults = true;
    }

    private void SelectSupplier(BusinessPartnerDto supplier)
    {
        selectedSupplier = supplier;
        goodsReceipt.CardCode = supplier.CardCode ?? string.Empty;
        supplierSearchTerm = supplier.DisplayName;
        showSupplierResults = false;
        validationErrors.Remove("CardCode");

        if (!string.IsNullOrWhiteSpace(supplier.Currency) && !string.Equals(supplier.Currency, "##", StringComparison.OrdinalIgnoreCase))
        {
            goodsReceipt.DocCurrency = supplier.Currency;
        }
    }

    private void ClearSupplier()
    {
        selectedSupplier = null;
        supplierSearchTerm = string.Empty;
        goodsReceipt.CardCode = string.Empty;
        showSupplierResults = true;
    }

    private void AddLine()
    {
        goodsReceipt.Lines.Add(new CreateGoodsReceiptPurchaseOrderLineRequest());
    }

    private void RemoveLine(int index)
    {
        if (goodsReceipt.Lines.Count == 1)
            return;

        goodsReceipt.Lines.RemoveAt(index);
        ClearLineValidation(index);
    }

    private void SyncProduct(int index)
    {
        if (index < 0 || index >= goodsReceipt.Lines.Count)
            return;

        var line = goodsReceipt.Lines[index];
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

        if (line.UnitPrice <= 0 && product.Price > 0)
        {
            line.UnitPrice = product.Price;
        }

        if (string.IsNullOrWhiteSpace(line.UoMCode))
        {
            line.UoMCode = product.UoM;
        }
    }

    private decimal CalculateLineTotal(CreateGoodsReceiptPurchaseOrderLineRequest line)
    {
        return Math.Max(0, line.Quantity * line.UnitPrice);
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

    private bool EnsureSupplierSelection()
    {
        var searchTerm = supplierSearchTerm.Trim();

        if (selectedSupplier is not null)
        {
            goodsReceipt.CardCode = selectedSupplier.CardCode ?? string.Empty;
            return !string.IsNullOrWhiteSpace(goodsReceipt.CardCode);
        }

        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            goodsReceipt.CardCode = string.Empty;
            return false;
        }

        var exactCodeMatch = suppliers.FirstOrDefault(supplier => string.Equals(supplier.CardCode, searchTerm, StringComparison.OrdinalIgnoreCase));
        if (exactCodeMatch is not null)
        {
            SelectSupplier(exactCodeMatch);
            return true;
        }

        var exactNameMatch = suppliers.FirstOrDefault(supplier =>
            string.Equals(supplier.CardName, searchTerm, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(supplier.DisplayName, searchTerm, StringComparison.OrdinalIgnoreCase));
        if (exactNameMatch is not null)
        {
            SelectSupplier(exactNameMatch);
            return true;
        }

        goodsReceipt.CardCode = searchTerm.Contains(" - ", StringComparison.Ordinal) ? searchTerm.Split(" - ", 2, StringSplitOptions.None)[0].Trim() : searchTerm;
        return !string.IsNullOrWhiteSpace(goodsReceipt.CardCode);
    }

    private bool Validate()
    {
        validationErrors.Clear();
        errorMessage = null;

        if (!EnsureSupplierSelection())
        {
            validationErrors["CardCode"] = "Supplier code is required.";
        }

        if (goodsReceipt.Lines.Count == 0)
        {
            validationErrors["Lines"] = "At least one goods receipt line is required.";
        }

        for (var index = 0; index < goodsReceipt.Lines.Count; index++)
        {
            var line = goodsReceipt.Lines[index];
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

        // Minted before the post so the draft saved on the render during it carries the key: a
        // reload mid-submit then resubmits under the same key and the API replays the document.
        goodsReceipt.ClientRequestId = IdempotentPost.EnsureKey(goodsReceipt.ClientRequestId);
        errorMessage = null;
        successMessage = null;

        try
        {
            GoodsReceiptPurchaseOrderDto? createdGoodsReceipt = null;
            var result = await Mediator.Send(new CreateGoodsReceiptPurchaseOrderCommand(goodsReceipt));

            result.SwitchFirst(
                value => createdGoodsReceipt = value,
                error => errorMessage = error.Description);

            if (createdGoodsReceipt is null)
            {
                await AuditService.LogAsync(
                    AuditActions.CreateGoodsReceiptPurchaseOrder,
                    "GoodsReceiptPurchaseOrder",
                    null,
                    $"Supplier {goodsReceipt.CardCode}",
                    false,
                    errorMessage);
                return;
            }

            successMessage = $"Goods receipt PO #{createdGoodsReceipt.DocNum} created successfully.";
            await ClearDraftAsync();

            await AuditService.LogAsync(
                AuditActions.CreateGoodsReceiptPurchaseOrder,
                "GoodsReceiptPurchaseOrder",
                createdGoodsReceipt.DocEntry.ToString(),
                $"Supplier {createdGoodsReceipt.CardCode}",
                true);

            await Task.Delay(900);
            NavigationManager.NavigateTo("/goods-receipt-pos");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected failure while posting goods receipt PO");
            errorMessage = "An unexpected error occurred while creating the goods receipt PO.";

            await AuditService.LogAsync(
                AuditActions.CreateGoodsReceiptPurchaseOrder,
                "GoodsReceiptPurchaseOrder",
                null,
                $"Supplier {goodsReceipt.CardCode}",
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
        NavigationManager.NavigateTo("/goods-receipt-pos");
    }
}