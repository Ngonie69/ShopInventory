using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.GoodsReceiptPurchaseOrders;
using ShopInventory.Features.PurchaseInvoices;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderFromSAPByDocEntry;
using ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrdersFromSAP;
using ShopInventory.Features.PurchaseQuotations;
using ShopInventory.Features.Quotations.Queries.GetQuotationFromSAPByDocEntry;
using ShopInventory.Features.Quotations.Queries.GetQuotationsFromSAP;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Quotations, purchase orders, purchase invoices, goods receipt POs and purchase quotations read
/// from SAP show a ZiG document in ZiG, and a USD document as they always did.
/// </summary>
/// <remarks>
/// SAP's DocTotal, VatSum, TotalDiscount and LineTotal are in the company's local currency (USD)
/// on every document. Each of these mappers copied them under the document's currency, so a ZiG
/// document showed its USD value: a thirtieth of the real figure at rate 30. The sales-order
/// version of this is <see cref="SalesOrderForeignCurrencyTotalsTests"/>.
///
/// <para>
/// The ZiG fixture is what a list or single read returns: the <c>$select</c>s carry
/// <c>DocTotalFc</c> but not <c>VatSumFc</c> or <c>TotalDiscountFC</c>, and the lines come back
/// whole with <c>RowTotalFC</c>. Tax and discount are converted at DocTotalFc/DocTotal = 30.
/// </para>
/// </remarks>
public sealed class SapDocumentForeignCurrencyTotalsTests
{
    private const int DocEntry = 77;

    /// <summary>The document's figures as the user should see them.</summary>
    private sealed record Shown(decimal DocTotal, decimal Tax, decimal Discount, decimal LineTotal);

    private static readonly Shown ZiGShown = new(DocTotal: 3000m, Tax: 391.20m, Discount: 150m, LineTotal: 2608.80m);
    private static readonly Shown UsdShown = new(DocTotal: 100m, Tax: 13.04m, Discount: 5m, LineTotal: 86.96m);

    public static TheoryData<string> Mappers => new()
    {
        "quotation",
        "quotation list",
        "purchase order",
        "purchase order list",
        "purchase invoice",
        "goods receipt PO",
        "purchase quotation"
    };

    [Theory]
    [MemberData(nameof(Mappers))]
    public async Task A_ZiG_document_shows_its_ZiG_totals(string mapper)
    {
        Assert.Equal(ZiGShown, await MapAsync(mapper, zig: true));
    }

    [Theory]
    [MemberData(nameof(Mappers))]
    public async Task A_USD_document_shows_the_totals_it_always_did(string mapper)
    {
        Assert.Equal(UsdShown, await MapAsync(mapper, zig: false));
    }

    /// <summary>
    /// A create handler maps SAP's POST response, which carries every field. SAP's own foreign tax
    /// wins over the converted figure, which can differ from it by rounding.
    /// </summary>
    [Fact]
    public void A_ZiG_create_response_uses_SAPs_own_foreign_tax_and_discount()
    {
        var invoice = Document<SAPPurchaseInvoice, SAPPurchaseInvoiceLine>(zig: true);
        invoice.VatSumFc = 391.25m;
        invoice.TotalDiscountFC = 149.99m;
        invoice.DocRate = 30m;

        var dto = PurchaseInvoiceMappings.MapFromSap(invoice);

        Assert.Equal(3000m, dto.DocTotal);
        Assert.Equal(391.25m, dto.VatSum);
        Assert.Equal(149.99m, dto.TotalDiscount);
    }

    [Fact]
    public async Task The_purchase_order_report_sums_ZiG_orders_in_ZiG()
    {
        var report = await BuildReportService(PurchaseOrdersForReport())
            .GetPurchaseOrderSummaryAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30));

        Assert.Equal(3000m, report.TotalOrderValueZIG);
        Assert.Equal(3000m, report.TotalPendingValueZIG);
        Assert.Equal(100m, report.TotalOrderValueUSD);
        Assert.Equal(100m, report.TotalPendingValueUSD);
        Assert.Equal(100m, report.AverageOrderValueUSD);

        var supplier = Assert.Single(report.BySupplier);
        Assert.Equal(3000m, supplier.TotalValueZIG);
        Assert.Equal(100m, supplier.TotalValueUSD);

        var day = Assert.Single(report.DailyBreakdown);
        Assert.Equal(3000m, day.TotalValueZIG);
        Assert.Equal(100m, day.TotalValueUSD);

        var product = Assert.Single(report.TopProducts);
        Assert.Equal(2608.80m, product.TotalCostZIG);
        Assert.Equal(86.96m, product.TotalCostUSD);
    }

    [Fact]
    public async Task The_profit_overview_shows_ZiG_purchase_cost_in_ZiG()
    {
        var report = await BuildReportService(PurchaseOrdersForReport())
            .GetProfitOverviewAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30));

        Assert.Equal(3000m, report.TotalPurchaseCostZIG);
        Assert.Equal(100m, report.TotalPurchaseCostUSD);
    }

    private static async Task<Shown> MapAsync(string mapper, bool zig)
    {
        switch (mapper)
        {
            case "quotation":
            {
                var sap = Client(getQuotation: Document<SAPQuotation, SAPQuotationLine>(zig));
                var dto = (await new GetQuotationFromSAPByDocEntryHandler(sap, NullLogger<GetQuotationFromSAPByDocEntryHandler>.Instance)
                    .Handle(new GetQuotationFromSAPByDocEntryQuery(DocEntry), CancellationToken.None)).Value;
                AssertSubTotal(dto.DocTotal, dto.TaxAmount, dto.SubTotal);
                return new Shown(dto.DocTotal, dto.TaxAmount, dto.DiscountAmount, Assert.Single(dto.Lines).LineTotal);
            }
            case "quotation list":
            {
                var sap = Client(getQuotation: Document<SAPQuotation, SAPQuotationLine>(zig));
                var list = (await new GetQuotationsFromSAPHandler(sap, NullLogger<GetQuotationsFromSAPHandler>.Instance)
                    .Handle(new GetQuotationsFromSAPQuery(1, 20, null, null, null), CancellationToken.None)).Value;
                var dto = Assert.Single(list.Quotations);
                AssertSubTotal(dto.DocTotal, dto.TaxAmount, dto.SubTotal);
                return new Shown(dto.DocTotal, dto.TaxAmount, dto.DiscountAmount, Assert.Single(dto.Lines).LineTotal);
            }
            case "purchase order":
            {
                var sap = Client(getPurchaseOrder: Document<SAPPurchaseOrder, SAPPurchaseOrderLine>(zig));
                var dto = (await new GetPurchaseOrderFromSAPByDocEntryHandler(sap, NullLogger<GetPurchaseOrderFromSAPByDocEntryHandler>.Instance)
                    .Handle(new GetPurchaseOrderFromSAPByDocEntryQuery(DocEntry), CancellationToken.None)).Value;
                AssertSubTotal(dto.DocTotal, dto.TaxAmount, dto.SubTotal);
                return new Shown(dto.DocTotal, dto.TaxAmount, dto.DiscountAmount, Assert.Single(dto.Lines).LineTotal);
            }
            case "purchase order list":
            {
                var sap = Client(getPurchaseOrder: Document<SAPPurchaseOrder, SAPPurchaseOrderLine>(zig));
                var list = (await new GetPurchaseOrdersFromSAPHandler(sap, NullLogger<GetPurchaseOrdersFromSAPHandler>.Instance)
                    .Handle(new GetPurchaseOrdersFromSAPQuery(1, 20, null, null, null), CancellationToken.None)).Value;
                var dto = Assert.Single(list.Orders);
                AssertSubTotal(dto.DocTotal, dto.TaxAmount, dto.SubTotal);
                return new Shown(dto.DocTotal, dto.TaxAmount, dto.DiscountAmount, Assert.Single(dto.Lines).LineTotal);
            }
            case "purchase invoice":
            {
                var dto = PurchaseInvoiceMappings.MapFromSap(Document<SAPPurchaseInvoice, SAPPurchaseInvoiceLine>(zig));
                return new Shown(dto.DocTotal, dto.VatSum, dto.TotalDiscount, Assert.Single(dto.Lines!).LineTotal);
            }
            case "goods receipt PO":
            {
                var dto = GoodsReceiptPurchaseOrderMappings.MapFromSap(
                    Document<SAPGoodsReceiptPurchaseOrder, SAPGoodsReceiptPurchaseOrderLine>(zig));
                return new Shown(dto.DocTotal, dto.VatSum, dto.TotalDiscount, Assert.Single(dto.Lines!).LineTotal);
            }
            case "purchase quotation":
            {
                var dto = PurchaseQuotationMappings.MapFromSap(Document<SAPPurchaseQuotation, SAPPurchaseQuotationLine>(zig));
                return new Shown(dto.DocTotal, dto.VatSum, dto.TotalDiscount, Assert.Single(dto.Lines!).LineTotal);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mapper), mapper, null);
        }
    }

    /// <summary>The subtotal is total less tax, both in the document's currency.</summary>
    private static void AssertSubTotal(decimal docTotal, decimal tax, decimal subTotal) =>
        Assert.Equal(docTotal - tax, subTotal);

    /// <summary>
    /// A document of any of the models, filled the way SAP answers a <c>$select</c> read: USD 100
    /// with 13.04 tax, 5 discount and one 86.96 line; as ZiG at rate 30 it also carries DocTotalFc
    /// 3,000 and RowTotalFC 2,608.80, and SAP fills no Fc field on a USD document.
    /// </summary>
    private static TDocument Document<TDocument, TLine>(bool zig)
        where TDocument : ISapDocumentAmounts, new()
        where TLine : ISapDocumentLineAmounts, new()
    {
        var line = new TLine();
        Set(line, "LineTotal", 86.96m);
        Set(line, "RowTotalFC", zig ? 2608.80m : 0m);
        Set(line, "ItemCode", "ITM001");
        Set(line, "Quantity", 1m);

        var document = new TDocument();
        Set(document, "DocEntry", DocEntry);
        Set(document, "CardCode", "SUP001");
        Set(document, "DocDate", "2026-11-02");
        Set(document, "DocCurrency", zig ? "ZiG" : "USD");
        Set(document, "DocumentStatus", "bost_Open");
        Set(document, "DocTotal", 100m);
        Set(document, "DocTotalFc", zig ? 3000m : 0m);
        Set(document, "VatSum", 13.04m);
        Set(document, "TotalDiscount", 5m);

        var lines = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(typeof(TLine)))!;
        lines.Add(line);
        Set(document, "DocumentLines", lines);
        return document;
    }

    private static void Set(object target, string property, object value)
    {
        var info = target.GetType().GetProperty(property)
            ?? throw new InvalidOperationException($"{target.GetType().Name} has no {property}");
        var type = Nullable.GetUnderlyingType(info.PropertyType) ?? info.PropertyType;
        info.SetValue(target, value is IConvertible && type != typeof(object) && !type.IsInstanceOfType(value)
            ? Convert.ChangeType(value, type)
            : value);
    }

    private static List<SAPPurchaseOrder> PurchaseOrdersForReport()
    {
        var zig = Document<SAPPurchaseOrder, SAPPurchaseOrderLine>(zig: true);
        var usd = Document<SAPPurchaseOrder, SAPPurchaseOrderLine>(zig: false);
        usd.DocEntry = DocEntry + 1;
        return [zig, usd];
    }

    private static ISAPServiceLayerClient Client(SAPQuotation? getQuotation = null, SAPPurchaseOrder? getPurchaseOrder = null) =>
        StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetQuotationByDocEntryAsync) => Task.FromResult(getQuotation),
            nameof(ISAPServiceLayerClient.GetPagedQuotationsFromSAPAsync) => Task.FromResult(new List<SAPQuotation> { getQuotation! }),
            nameof(ISAPServiceLayerClient.GetPurchaseOrderByDocEntryAsync) => Task.FromResult(getPurchaseOrder),
            nameof(ISAPServiceLayerClient.CountPurchaseOrdersAsync) => Task.FromResult(1),
            nameof(ISAPServiceLayerClient.GetPurchaseOrderPageAsync) => Task.FromResult(new List<SAPPurchaseOrder> { getPurchaseOrder! }),
            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

    private static ReportService BuildReportService(List<SAPPurchaseOrder> purchaseOrders)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) =>
                Task.FromResult(new List<Dictionary<string, object?>>()),
            nameof(ISAPServiceLayerClient.GetIncomingPaymentsByDateRangeAsync) =>
                Task.FromResult(new List<IncomingPayment>()),
            nameof(ISAPServiceLayerClient.GetCreditNotesByDateRangeAsync) =>
                Task.FromResult(new List<SAPCreditNote>()),
            nameof(ISAPServiceLayerClient.GetPurchaseOrdersByDateRangeAsync) =>
                Task.FromResult(purchaseOrders),
            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

        return new ReportService(sap, new MemoryCache(new MemoryCacheOptions()), NullLogger<ReportService>.Instance);
    }
}
