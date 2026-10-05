using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Every receipt states the channel and warehouse it was raised in, so the fiscalisation platform's Receipts
/// drawer can tell a ShopInventory sale from a document the SAP bridge sent.
/// </summary>
public sealed class FiscalReceiptSourceTests
{
    [Theory]
    [InlineData(SaleSourceSystems.ShopTill, "Shop till")]
    [InlineData(SaleSourceSystems.Vending, "Vending")]
    [InlineData(SaleSourceSystems.VanSales, "Van")]
    [InlineData(SaleSourceSystems.VanSalesOnline, "Van")]
    [InlineData(SaleSourceSystems.LegacyDesktop, "Desktop app")]
    [InlineData(null, "ShopInventory")]
    [InlineData("SomeNewSource", "SomeNewSource")]
    public void A_sale_source_names_its_channel(string? sourceSystem, string channel)
    {
        Assert.Equal(channel, FiscalReceiptSource.ForSale(sourceSystem, "kefgrs ").Channel);
        Assert.Equal("KEFGRS", FiscalReceiptSource.ForSale(sourceSystem, "kefgrs ").Location);
    }

    [Fact]
    public void A_sap_document_is_placed_by_its_distinct_line_warehouses()
    {
        var source = FiscalReceiptSource.ForSapDocument(FiscalReceiptSource.SalesInvoice, ["KEFSHOP", null, "kefshop", "KEFGRS"]);

        Assert.Equal("KEFSHOP, KEFGRS", source.Location);
    }

    [Fact]
    public async Task A_till_sale_sends_its_channel_and_warehouse()
    {
        var submitted = await CaptureAsync<SubmitReceiptApiRequest>(nameof(IFiscalisationApiClient.SubmitReceiptAsync), service =>
            service.FiscalizePreSapInvoiceAsync(
                Invoice(),
                "GRC-FAC-20261005-05E88624CE39",
                source: FiscalReceiptSource.ForSale(SaleSourceSystems.ShopTill, "KEFGRS")));

        Assert.Equal("Shop till", submitted.SourceChannel);
        Assert.Equal("KEFGRS", submitted.SourceLocation);
    }

    [Fact]
    public async Task A_document_fiscalised_from_sap_says_shopinventory_raised_it()
    {
        var invoice = Invoice();
        invoice.DocEntry = 501;
        invoice.DocNum = 784900;
        invoice.Lines![0].WarehouseCode = "KEFSHOP";

        var submitted = await CaptureAsync<SapFiscaliseReceiptApiRequest>(nameof(IFiscalisationApiClient.SubmitSapReceiptAsync), service =>
            service.FiscalizeInvoiceAsync(invoice));

        Assert.Equal(FiscalReceiptSource.SalesInvoice, submitted.Receipt!.SourceChannel);
        Assert.Equal("KEFSHOP", submitted.Receipt.SourceLocation);
    }

    [Fact]
    public void A_desktop_credit_carries_its_saved_source_to_the_platform()
    {
        var plan = new DesktopCreditPlan(
            new DesktopCreditSource("GRC-FAC-1", "USD", 10m, 36189, 7, 1866, 449164855, []),
            [],
            new SubmitReceiptApiRequest
            {
                InvoiceNo = "DCN-1",
                SourceChannel = FiscalReceiptSource.DesktopCreditNote,
                SourceLocation = "KEFGRS"
            },
            10m);

        var request = PlatformDesktopCreditGateway.BuildRequest(plan, new FiscalisationSettings());

        Assert.Equal(FiscalReceiptSource.DesktopCreditNote, request.SourceChannel);
        Assert.Equal("KEFGRS", request.SourceLocation);
    }

    private static InvoiceDto Invoice() => new()
    {
        DocDate = "2026-10-05",
        DocCurrency = "USD",
        DocTotal = 1m,
        Lines = [new InvoiceLineDto { LineNum = 1, ItemCode = "A", Quantity = 1, GrossPrice = 1m }]
    };

    private static async Task<T> CaptureAsync<T>(string clientMethod, Func<FiscalizationService, Task> act)
        where T : class
    {
        T? captured = null;
        var client = StubProxy.For<IFiscalisationApiClient>((method, args) =>
        {
            if (method.Name != clientMethod)
            {
                throw new InvalidOperationException($"IFiscalisationApiClient.{method.Name} was not expected.");
            }

            captured = (T)args![0]!;
            return Task.FromResult(new SubmitReceiptApiResponse { Success = true });
        });

        var service = new FiscalizationService(
            client,
            StubProxy.For<IFiscalDeviceConfigCache>((_, _) => Task.FromResult<FiscalConfigApiResponse?>(null)),
            Options.Create(new FiscalisationSettings { Enabled = true }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance);

        await act(service);
        return captured ?? throw new InvalidOperationException($"{clientMethod} was never called.");
    }
}
