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

    // Inventory Transfer Request Operations
    /// <summary>
    /// Creates a new inventory transfer request in SAP Business One.
    /// Transfer requests are draft documents that require approval before becoming actual transfers.
    /// </summary>
    Task<InventoryTransferRequest> CreateInventoryTransferRequestAsync(CreateTransferRequestDto request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an inventory transfer request by document entry
    /// </summary>
    Task<InventoryTransferRequest?> GetInventoryTransferRequestByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all inventory transfer requests to a specific warehouse
    /// </summary>
    Task<List<InventoryTransferRequest>> GetInventoryTransferRequestsByWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets inventory transfer requests with pagination
    /// </summary>
    Task<List<InventoryTransferRequest>> GetPagedInventoryTransferRequestsAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts an inventory transfer request to an actual inventory transfer.
    /// This fetches the request details and creates a new transfer with the same line items.
    /// </summary>
    Task<InventoryTransfer> ConvertTransferRequestToTransferAsync(int requestDocEntry, CancellationToken cancellationToken = default);

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

    // Price Operations (Legacy - deprecated)
    [Obsolete("Use GetPricesByPriceListAsync with specific price list number instead")]
    Task<List<ItemPriceDto>> GetItemPricesAsync(CancellationToken cancellationToken = default);
    [Obsolete("Use GetItemPriceFromListAsync with specific price list number instead")]
    Task<List<ItemPriceDto>> GetItemPriceByCodeAsync(string itemCode, CancellationToken cancellationToken = default);

    // Price Operations (New - Dynamic Price Lists)
    Task<List<PriceListDto>> GetPriceListsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets prices for all items from a specific price list
    /// </summary>
    Task<List<ItemPriceByListDto>> GetPricesByPriceListAsync(int priceListNum, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the price for a specific item from a specific price list
    /// </summary>
    Task<ItemPriceByListDto?> GetItemPriceFromListAsync(string itemCode, int priceListNum, CancellationToken cancellationToken = default);

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

    // Cost Centre (Profit Center) Operations
    /// <summary>
    /// Gets all active cost centres (profit centers) from SAP Business One.
    /// These rarely change and should be cached locally.
    /// </summary>
    Task<List<CostCentreDto>> GetCostCentresAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets cost centres for a specific dimension (1-5)
    /// </summary>
    Task<List<CostCentreDto>> GetCostCentresByDimensionAsync(int dimension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific cost centre by code
    /// </summary>
    Task<CostCentreDto?> GetCostCentreByCodeAsync(string centerCode, CancellationToken cancellationToken = default);

    // Sales Order Operations (from SAP)
    Task<List<SAPSalesOrder>> GetSalesOrdersAsync(CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetPagedSalesOrdersAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPSalesOrder?> GetSalesOrderByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetSalesOrdersByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetSalesOrdersByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetSalesOrdersCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);

    // Credit Note/Credit Memo Operations (from SAP)
    Task<List<SAPCreditNote>> GetCreditNotesAsync(CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetPagedCreditNotesAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPCreditNote?> GetCreditNoteByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetCreditNotesByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetCreditNotesByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetCreditNotesByInvoiceAsync(int invoiceDocEntry, CancellationToken cancellationToken = default);
    Task<int> GetCreditNotesCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a credit note (A/R Credit Memo) in SAP Business One
    /// </summary>
    Task<SAPCreditNote> CreateCreditNoteAsync(CreateCreditNoteRequest request, CancellationToken cancellationToken = default);

    // Exchange Rate Operations
    /// <summary>
    /// Gets all exchange rates from SAP Business One
    /// </summary>
    Task<List<SAPExchangeRate>> GetExchangeRatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current exchange rate for a specific currency against the local currency from SAP
    /// </summary>
    Task<SAPExchangeRate?> GetExchangeRateAsync(string currency, DateTime? date = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets currencies defined in SAP Business One
    /// </summary>
    Task<List<SAPCurrency>> GetCurrenciesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the connection to SAP Business One Service Layer
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}
