using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

public interface ISAPServiceLayerClient
{
    // Inventory Transfer Operations
    Task<List<InventoryTransfer>> GetInventoryTransfersToWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetPagedInventoryTransfersToWarehouseAsync(string warehouseCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetInventoryTransfersByDateAsync(string warehouseCode, DateTime date, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetInventoryTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<InventoryTransfer?> GetInventoryTransferByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new inventory transfer in SAP Business One.
    /// CRITICAL: Validates stock availability to prevent negative quantities.
    /// </summary>
    Task<InventoryTransfer> CreateInventoryTransferAsync(CreateInventoryTransferRequest request, CancellationToken cancellationToken = default);

    // Invoice Operations
    Task<Invoice> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default);
    Task<Invoice?> GetInvoiceByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoicesByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoicesByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetPagedInvoicesAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    // Product/Item Operations
    Task<List<Item>> GetAllItemsAsync(CancellationToken cancellationToken = default);
    Task<List<Item>> GetItemsInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<Item>> GetPagedItemsInWarehouseAsync(string warehouseCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<Item?> GetItemByCodeAsync(string itemCode, CancellationToken cancellationToken = default);
    Task<List<BatchNumber>> GetBatchNumbersForItemInWarehouseAsync(string itemCode, string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<BatchNumber>> GetAllBatchNumbersInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);

    // Price Operations
    Task<List<ItemPriceDto>> GetItemPricesAsync(CancellationToken cancellationToken = default);
    Task<List<ItemPriceDto>> GetItemPriceByCodeAsync(string itemCode, CancellationToken cancellationToken = default);

    // Incoming Payment Operations
    Task<List<IncomingPayment>> GetIncomingPaymentsAsync(CancellationToken cancellationToken = default);
    Task<List<IncomingPayment>> GetPagedIncomingPaymentsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<IncomingPayment?> GetIncomingPaymentByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<IncomingPayment?> GetIncomingPaymentByDocNumAsync(int docNum, CancellationToken cancellationToken = default);
    Task<List<IncomingPayment>> GetIncomingPaymentsByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<IncomingPayment>> GetIncomingPaymentsByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new incoming payment in SAP Business One.
    /// CRITICAL: Validates all amounts are non-negative.
    /// </summary>
    Task<IncomingPayment> CreateIncomingPaymentAsync(CreateIncomingPaymentRequest request, CancellationToken cancellationToken = default);

    // Stock Quantity Operations
    Task<List<StockQuantityDto>> GetStockQuantitiesInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<StockQuantityDto>> GetPagedStockQuantitiesInWarehouseAsync(string warehouseCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<Dictionary<string, PackagingMaterialStockDto>> GetPackagingMaterialStockAsync(IEnumerable<string> itemCodes, string warehouseCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that sufficient stock is available for all items in an invoice request.
    /// Returns a list of validation errors if any items have insufficient stock.
    /// </summary>
    Task<List<StockValidationError>> ValidateStockAvailabilityAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default);

    // Sales Quantity Operations
    Task<List<SalesQuantityDto>> GetSalesQuantitiesByWarehouseAsync(string warehouseCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoicesByWarehouseAndDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);

    // Warehouse Operations
    Task<List<WarehouseDto>> GetWarehousesAsync(CancellationToken cancellationToken = default);

    // Business Partner Operations
    Task<List<BusinessPartnerDto>> GetBusinessPartnersAsync(CancellationToken cancellationToken = default);
    Task<List<BusinessPartnerDto>> GetBusinessPartnersByTypeAsync(string cardType, CancellationToken cancellationToken = default);
    Task<List<BusinessPartnerDto>> SearchBusinessPartnersAsync(string searchTerm, CancellationToken cancellationToken = default);
    Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode, CancellationToken cancellationToken = default);

    // G/L Account Operations
    Task<List<GLAccountDto>> GetGLAccountsAsync(CancellationToken cancellationToken = default);
    Task<List<GLAccountDto>> GetGLAccountsByTypeAsync(string accountType, CancellationToken cancellationToken = default);
    Task<GLAccountDto?> GetGLAccountByCodeAsync(string accountCode, CancellationToken cancellationToken = default);
}
