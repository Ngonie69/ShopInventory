using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// A receipt carries buyer details only when FDMS will record them: a registered name and a TIN.
/// </summary>
/// <remarks>
/// For a document already in SAP the platform reads the buyer itself (TIN from OCRD.CardFName, VAT number
/// from LicTradNum, the bill-to address), but only when the request names no buyer. Every invoice used to
/// send a buyer holding just the customer's name, which replaced that SAP buyer, and FDMS then dropped a
/// buyer with no TIN, so VAT-registered customers were filed with no buyer at all.
/// </remarks>
public sealed class FiscalReceiptBuyerTests
{
    [Fact]
    public async Task An_invoice_from_sap_leaves_the_buyer_to_the_platform()
    {
        var submitted = await CaptureAsync<SapFiscaliseReceiptApiRequest>(
            nameof(IFiscalisationApiClient.SubmitSapReceiptAsync),
            service => service.FiscalizeInvoiceAsync(SapInvoice(), new CustomerFiscalDetails { CustomerName = "Sai Mart Nketa 7" }));

        Assert.Null(submitted.Receipt!.Buyer);
    }

    [Fact]
    public async Task An_invoice_from_sap_sends_no_buyer_even_with_full_details()
    {
        var submitted = await CaptureAsync<SapFiscaliseReceiptApiRequest>(
            nameof(IFiscalisationApiClient.SubmitSapReceiptAsync),
            service => service.FiscalizeInvoiceAsync(SapInvoice(), RegisteredBuyer()));

        Assert.Null(submitted.Receipt!.Buyer);
    }

    [Fact]
    public async Task A_credit_note_from_sap_leaves_the_buyer_to_the_platform()
    {
        var submitted = await CaptureAsync<SapFiscaliseReceiptApiRequest>(
            nameof(IFiscalisationApiClient.SubmitSapReceiptAsync),
            service => service.FiscalizeCreditNoteAsync(SapInvoice(), "784900", new CustomerFiscalDetails { CustomerName = "Sai Mart Nketa 7" }));

        Assert.Null(submitted.Receipt!.Buyer);
    }

    [Fact]
    public async Task A_sale_not_in_sap_sends_no_buyer_without_a_tin()
    {
        var submitted = await CaptureAsync<SubmitReceiptApiRequest>(
            nameof(IFiscalisationApiClient.SubmitReceiptAsync),
            service => service.FiscalizePreSapInvoiceAsync(
                PreSapInvoice(),
                "GRC-FAC-20261022-000001",
                new CustomerFiscalDetails { CustomerName = "Cash customer", Telephone = "0772000000" }));

        Assert.Null(submitted.Buyer);
    }

    [Fact]
    public async Task A_sale_not_in_sap_sends_a_buyer_that_has_a_name_and_a_tin()
    {
        var submitted = await CaptureAsync<SubmitReceiptApiRequest>(
            nameof(IFiscalisationApiClient.SubmitReceiptAsync),
            service => service.FiscalizePreSapInvoiceAsync(PreSapInvoice(), "GRC-FAC-20261022-000002", RegisteredBuyer()));

        Assert.NotNull(submitted.Buyer);
        Assert.Equal("Sai Mart Nketa 7", submitted.Buyer!.RegisterName);
        Assert.Equal("2000123456", submitted.Buyer.Tin);
        Assert.Equal("220123456", submitted.Buyer.VatNumber);
    }

    private static CustomerFiscalDetails RegisteredBuyer() => new()
    {
        CustomerName = "Sai Mart Nketa 7",
        BPN = "2000123456",
        VatNumber = "220123456",
        Address = "12 Nketa Drive"
    };

    private static InvoiceDto SapInvoice() => new()
    {
        DocEntry = 501,
        DocNum = 784900,
        CardName = "Sai Mart Nketa 7",
        Lines = [new InvoiceLineDto { LineNum = 1, ItemCode = "A", Quantity = 1, GrossPrice = 1m, WarehouseCode = "DISPICE" }]
    };

    private static InvoiceDto PreSapInvoice() => new()
    {
        DocDate = "2026-10-22",
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
