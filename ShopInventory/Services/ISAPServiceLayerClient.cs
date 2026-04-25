using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

public interface ISAPServiceLayerClient
{
    // Inventory Transfer Operations
    Task<List<InventoryTransfer>> GetInventoryTransfersToWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetPagedInventoryTransfersToWarehouseAsync(string warehouseCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetPagedInventoryTransfersByOffsetAsync(string warehouseCode, int skip, int pageSize, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetInventoryTransfersByDateAsync(string warehouseCode, DateTime date, CancellationToken cancellationToken = default);
    Task<List<InventoryTransfer>> GetInventoryTransfersByDateRangeAsync(string warehouseCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetInventoryTransfersCountAsync(string warehouseCode, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<InventoryTransfer?> GetInventoryTransferByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new inventory transfer in SAP Business One.
    /// CRITICAL: Validates stock availability to prevent negative quantities.
    /// </summary>
    Task<InventoryTransfer> CreateInventoryTransferAsync(CreateInventoryTransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new inventory transfer in SAP Business One, reusing pre-fetched data from validation
    /// to avoid redundant SAP API calls for item metadata, batches, and serial numbers.
    /// </summary>
    Task<InventoryTransfer> CreateInventoryTransferAsync(CreateInventoryTransferRequest request, TransferPreFetchedData? preFetchedData, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Closes an inventory transfer request in SAP so it cannot be converted again.
    /// </summary>
    Task CloseInventoryTransferRequestAsync(int docEntry, CancellationToken cancellationToken = default);

    // Invoice Operations
    Task<Invoice> CreateInvoiceAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default);
    Task<Invoice?> GetInvoiceByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<Invoice?> GetInvoiceByDocNumAsync(int docNum, CancellationToken cancellationToken = default);
    Task<Invoice?> GetInvoiceByVanSaleOrderAsync(string vanSaleOrder, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoicesByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoicesByCustomerAsync(string cardCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoicesByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetInvoiceHeadersByDateRangeAsync(DateTime fromDate, DateTime toDate, List<string>? excludeCardCodes = null, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetPagedInvoicesAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetPagedInvoicesByOffsetAsync(int skip, int pageSize, CancellationToken cancellationToken = default);
    Task<List<Invoice>> GetPagedInvoicesByOffsetAsync(int skip, int pageSize, int? docNum = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<int> GetInvoicesCountAsync(int? docNum = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);

    // Product/Item Operations
    Task<List<Item>> GetAllItemsAsync(CancellationToken cancellationToken = default);
    [Obsolete("Use GetPagedItemsInWarehouseAsync for UI/list paths. This full-fetch method scans all warehouse batches and must only be used by explicit background/export flows.", true)]
    Task<List<Item>> GetItemsInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<(List<Item> Items, bool HasMore)> GetPagedItemsInWarehouseAsync(string warehouseCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<Item?> GetItemByCodeAsync(string itemCode, CancellationToken cancellationToken = default);
    Task<List<BatchNumber>> GetBatchNumbersForItemInWarehouseAsync(string itemCode, string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<BatchNumber>> GetBatchNumbersForItemsInWarehouseAsync(IEnumerable<string> itemCodes, string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<BatchNumber>> GetAllBatchNumbersInWarehouseAsync(string warehouseCode, CancellationToken cancellationToken = default);
    Task<List<SerialNumber>> GetSerialNumbersForItemInWarehouseAsync(string itemCode, string warehouseCode, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Gets prices for specific items using a customer's assigned price list (from OCRD.ListNum).
    /// Combines BP lookup + price fetch into a single SAP SQL query for efficiency.
    /// </summary>
    Task<List<ItemPriceByListDto>> GetItemPricesForCustomerAsync(string cardCode, IEnumerable<string> itemCodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets special prices assigned to a specific business partner from SAP (OSPP table).
    /// These override the BP's default price list prices for specific items.
    /// </summary>
    Task<Dictionary<string, decimal>> GetSpecialPricesForBPAsync(string cardCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets special prices assigned to a specific business partner from SAP (OSPP table)
    /// for only the requested item codes.
    /// </summary>
    Task<Dictionary<string, decimal>> GetSpecialPricesForBPAsync(string cardCode, IEnumerable<string> itemCodes, CancellationToken cancellationToken = default);

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

    // Payment Terms Operations
    Task<PaymentTermsDto?> GetPaymentTermsByCodeAsync(int groupNumber, CancellationToken cancellationToken = default);

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

    // Purchase Order Operations (from SAP)
    Task<List<SAPPurchaseOrder>> GetPurchaseOrdersFromSAPAsync(CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseOrder>> GetPagedPurchaseOrdersFromSAPAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPPurchaseOrder?> GetPurchaseOrderByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseOrder>> GetPurchaseOrdersBySupplierAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseOrder>> GetPurchaseOrdersByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetPurchaseOrdersCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);

    // Purchase Request Operations (from SAP)
    Task<List<SAPPurchaseRequest>> GetPagedPurchaseRequestsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPPurchaseRequest?> GetPurchaseRequestByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseRequest>> GetPurchaseRequestsByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetPurchaseRequestsCountAsync(DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<SAPPurchaseRequest> CreatePurchaseRequestAsync(CreatePurchaseRequestRequest request, CancellationToken cancellationToken = default);

    // Purchase Quotation Operations (from SAP)
    Task<List<SAPPurchaseQuotation>> GetPagedPurchaseQuotationsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPPurchaseQuotation?> GetPurchaseQuotationByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseQuotation>> GetPurchaseQuotationsBySupplierAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseQuotation>> GetPurchaseQuotationsByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetPurchaseQuotationsCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<SAPPurchaseQuotation> CreatePurchaseQuotationAsync(CreatePurchaseQuotationRequest request, CancellationToken cancellationToken = default);

    // Goods Receipt Purchase Order Operations (from SAP)
    Task<List<SAPGoodsReceiptPurchaseOrder>> GetPagedGoodsReceiptPurchaseOrdersAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPGoodsReceiptPurchaseOrder?> GetGoodsReceiptPurchaseOrderByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPGoodsReceiptPurchaseOrder>> GetGoodsReceiptPurchaseOrdersBySupplierAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPGoodsReceiptPurchaseOrder>> GetGoodsReceiptPurchaseOrdersByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetGoodsReceiptPurchaseOrdersCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<SAPGoodsReceiptPurchaseOrder> CreateGoodsReceiptPurchaseOrderAsync(CreateGoodsReceiptPurchaseOrderRequest request, CancellationToken cancellationToken = default);

    // Purchase Invoice Operations (from SAP)
    Task<List<SAPPurchaseInvoice>> GetPagedPurchaseInvoicesAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPPurchaseInvoice?> GetPurchaseInvoiceByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseInvoice>> GetPurchaseInvoicesBySupplierAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPPurchaseInvoice>> GetPurchaseInvoicesByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetPurchaseInvoicesCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<SAPPurchaseInvoice> CreatePurchaseInvoiceAsync(CreatePurchaseInvoiceRequest request, CancellationToken cancellationToken = default);

    // Sales Order Operations (from SAP)
    Task<List<SAPSalesOrder>> GetSalesOrdersAsync(CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetPagedSalesOrdersAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetPagedSalesOrdersByOffsetAsync(int skip, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPSalesOrder?> GetSalesOrderByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetSalesOrdersByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetSalesOrdersByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetSalesOrderHeadersAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, int skip = 0, int pageSize = 20, string? documentStatus = null, string? cancelled = null, CancellationToken cancellationToken = default);
    Task<List<SAPSalesOrder>> GetSalesOrderHeadersByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetSalesOrdersCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, string? documentStatus = null, string? cancelled = null, CancellationToken cancellationToken = default);
    Task<SAPSalesOrder> CreateSalesOrderAsync(ShopInventory.Models.Entities.SalesOrderEntity order, CancellationToken cancellationToken = default);
    Task<List<Dictionary<string, object?>>> ExecuteRawSqlQueryAsync(string queryCode, string queryName, string sqlText, CancellationToken cancellationToken = default);

    // Credit Note/Credit Memo Operations (from SAP)
    Task<List<SAPCreditNote>> GetCreditNotesAsync(CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetPagedCreditNotesAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPCreditNote?> GetCreditNoteByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetCreditNotesByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPCreditNote>> GetCreditNotesByCustomerAsync(string cardCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Tests the connection to SAP Business One Service Layer using specific credentials
    /// </summary>
    Task<bool> TestConnectionWithCredentialsAsync(
        string serviceLayerUrl, string companyDb, string userName, string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicitly logs out the current SAP session to free the server-side session slot.
    /// </summary>
    Task LogoutAsync(CancellationToken cancellationToken = default);

    // SAP Attachment Operations
    /// <summary>
    /// Copies a file to the SAP attachments share and creates an Attachments2 metadata record.
    /// Returns the AbsoluteEntry of the created attachment.
    /// </summary>
    Task<int> UploadAttachmentToSAPAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a file to an existing SAP Attachments2 record.
    /// Returns the unchanged AbsoluteEntry of the attachment.
    /// </summary>
    Task<int> AppendAttachmentToSAPAsync(int attachmentEntry, Stream fileStream, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links an existing SAP attachment to an invoice by updating its AttachmentEntry field.
    /// </summary>
    Task LinkAttachmentToInvoiceAsync(int invoiceDocEntry, int attachmentEntry, CancellationToken cancellationToken = default);

    // Quotation Operations (from SAP)
    Task<List<SAPQuotation>> GetQuotationsFromSAPAsync(CancellationToken cancellationToken = default);
    Task<List<SAPQuotation>> GetPagedQuotationsFromSAPAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SAPQuotation?> GetQuotationByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<List<SAPQuotation>> GetQuotationsByCustomerAsync(string cardCode, CancellationToken cancellationToken = default);
    Task<List<SAPQuotation>> GetQuotationsByDateRangeAsync(DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default);
    Task<int> GetQuotationsCountAsync(string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
}
