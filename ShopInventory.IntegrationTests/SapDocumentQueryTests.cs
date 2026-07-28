using ShopInventory.Models;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer to accept every document query this codebase builds.
/// </summary>
/// <remarks>
/// This is the gap that let three broken URLs reach main. The unit tests check that each $select
/// names only fields present in the committed metadata, and that the literals interpolate — neither
/// proves SAP accepts the finished URL. An unknown field or a malformed filter is a 400, which
/// surfaces here as an exception carrying SAP's own error text.
///
/// Every test is read-only. Nothing here creates, patches or cancels a document, and the SQL-backed
/// methods are deliberately excluded: those provision SQLQueries objects, which this SAP instance
/// cannot practically delete, so running them repeatedly would leave litter behind.
/// </remarks>
[Collection("SAP")]
public class SapDocumentQueryTests(SapClientFixture fixture)
{
    private static readonly DateTime FromDate = DateTime.UtcNow.Date.AddDays(-30);
    private static readonly DateTime ToDate = DateTime.UtcNow.Date;

    [SapFact]
    public async Task Purchase_order_list_is_accepted()
        => await ShouldBeAccepted(() => fixture.Client.GetPurchaseOrdersFromSAPAsync());

    [SapFact]
    public async Task Credit_note_list_is_accepted()
        => await ShouldBeAccepted(() => fixture.Client.GetCreditNotesAsync());

    [SapFact]
    public async Task Quotation_list_is_accepted()
        => await ShouldBeAccepted(() => fixture.Client.GetQuotationsFromSAPAsync());

    [SapFact]
    public async Task Sales_order_list_is_accepted()
        => await ShouldBeAccepted(() => fixture.Client.GetSalesOrdersAsync());

    [SapFact]
    public async Task Paged_document_lists_are_accepted()
    {
        await ShouldBeAccepted(() => fixture.Client.GetPagedPurchaseRequestsAsync(1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetPagedPurchaseQuotationsAsync(1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetPagedGoodsReceiptPurchaseOrdersAsync(1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetPagedPurchaseInvoicesAsync(1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetPagedCreditNotesAsync(1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetPagedQuotationsFromSAPAsync(1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetPagedIncomingPaymentsAsync(1, 5));
    }

    [SapFact]
    public async Task Date_ranged_document_lists_are_accepted()
    {
        await ShouldBeAccepted(() => fixture.Client.GetPurchaseOrdersByDateRangeAsync(FromDate, ToDate));
        await ShouldBeAccepted(() => fixture.Client.GetPurchaseInvoicesByDateRangeAsync(FromDate, ToDate));
        await ShouldBeAccepted(() => fixture.Client.GetCreditNotesByDateRangeAsync(FromDate, ToDate));
        await ShouldBeAccepted(() => fixture.Client.GetQuotationsByDateRangeAsync(FromDate, ToDate));
        await ShouldBeAccepted(() => fixture.Client.GetIncomingPaymentsByDateRangeAsync(FromDate, ToDate));
    }

    [SapFact]
    public async Task Inventory_transfer_queries_are_accepted()
    {
        var warehouses = await fixture.Client.GetWarehousesAsync();
        var warehouseCode = warehouses.FirstOrDefault()?.WarehouseCode;
        Assert.False(
            string.IsNullOrWhiteSpace(warehouseCode),
            "SAP returned no warehouses, so the transfer queries cannot be exercised. Point these tests at a company with data.");

        await ShouldBeAccepted(() => fixture.Client.GetPagedInventoryTransfersToWarehouseAsync(warehouseCode!, 1, 5));
        await ShouldBeAccepted(() => fixture.Client.GetInventoryTransfersByDateRangeAsync(warehouseCode!, FromDate, ToDate));
        await ShouldBeAccepted(() => fixture.Client.GetInventoryTransferRequestsByWarehouseAsync(warehouseCode!));
    }

    [SapFact]
    public async Task Reference_data_queries_are_accepted()
    {
        await ShouldBeAccepted(() => fixture.Client.GetWarehousesAsync());
        await ShouldBeAccepted(() => fixture.Client.GetGLAccountsAsync());
        await ShouldBeAccepted(() => fixture.Client.GetCostCentresAsync());
        await ShouldBeAccepted(() => fixture.Client.GetCurrenciesAsync());
        await ShouldBeAccepted(() => fixture.Client.GetCostCentresByDimensionAsync(1));
    }

    [SapFact]
    public async Task Batched_customer_queries_are_accepted()
    {
        var cardCodes = await TakeCustomerCodesAsync(3);
        Assert.False(
            cardCodes.Count == 0,
            "SAP returned no customers, so the batched customer queries cannot be exercised. Point these tests at a company with data.");

        await ShouldBeAccepted(() => fixture.Client.GetInvoicesByCustomersAsync(cardCodes, FromDate, ToDate));
        await ShouldBeAccepted(() => fixture.Client.GetOpenInvoicesByCustomersAsync(cardCodes));
        await ShouldBeAccepted(() => fixture.Client.GetIncomingPaymentsByCustomersAsync(cardCodes, FromDate, ToDate));
    }

    [SapFact]
    public async Task Batched_order_number_probe_is_accepted()
    {
        // Order numbers that cannot exist: the point is that SAP accepts the ORed filter, not that
        // it matches anything.
        var resolved = await fixture.Client.GetSalesOrdersByOrderNumbersAsync(
            ["INTEGRATION-TEST-DOES-NOT-EXIST-1", "INTEGRATION-TEST-DOES-NOT-EXIST-2"]);

        Assert.Empty(resolved);
    }

    [SapFact]
    public async Task Business_partner_search_accepts_a_quote_in_the_term()
    {
        // The escaping fix. Unescaped, the quote closes the literal and SAP rejects the filter.
        await ShouldBeAccepted(() => fixture.Client.SearchBusinessPartnersAsync("O'Brien"));
        await ShouldBeAccepted(() => fixture.Client.SearchBusinessPartnersAsync("a&b"));
    }

    [SapFact]
    public async Task Item_lookup_is_accepted_and_returns_the_bound_fields()
    {
        var items = await fixture.Client.GetAllItemsAsync();
        var itemCode = items.FirstOrDefault()?.ItemCode;
        Assert.False(
            string.IsNullOrWhiteSpace(itemCode),
            "SAP returned no items, so the item lookup cannot be exercised. Point these tests at a company with data.");

        var item = await fixture.Client.GetItemByCodeAsync(itemCode!);

        Assert.NotNull(item);
        Assert.Equal(itemCode, item.ItemCode);
        // $select must not have dropped a field the model binds.
        Assert.False(string.IsNullOrWhiteSpace(item.ItemName), "ItemName came back empty; check ItemSelect.");
    }

    private async Task<List<string>> TakeCustomerCodesAsync(int count)
    {
        var partners = await fixture.Client.GetBusinessPartnersAsync();
        return [.. partners
            .Where(partner => !string.IsNullOrWhiteSpace(partner.CardCode))
            .Select(partner => partner.CardCode!)
            .Take(count)];
    }

    /// <summary>
    /// Runs a query and fails with SAP's own words when it is rejected. The result may legitimately
    /// be empty — a test company need not hold any given document type — so nothing asserts on
    /// count.
    /// </summary>
    private static async Task ShouldBeAccepted<T>(Func<Task<T>> query)
    {
        try
        {
            await query();
        }
        catch (Exception ex)
        {
            Assert.Fail($"SAP rejected the query: {ex.Message}");
        }
    }
}
