using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The two guards against a receipt that declares a different amount from its SAP invoice: the refusal to
/// sign a net price as the gross, and the comparison once SAP has the invoice.
/// </summary>
public sealed class FiscalReceiptAmountCheckTests
{
    /// <summary>784863: SAP charged 8.70 (10 at 0.869946), the receipt declared 7.50.</summary>
    [Fact]
    public void Agrees_RefusesTheReceiptThatFiledTheNetPriceAsTheGross()
    {
        var invoice = SapInvoice(docTotal: 8.70m, (10m, 0.869946m, 7.53m));

        Assert.Equal(8.70m, FiscalReceiptAmountCheck.RepricedAtReceiptPrices(invoice));
        Assert.False(FiscalReceiptAmountCheck.Agrees(7.50m, invoice.DocTotal, 8.70m));
    }

    /// <summary>
    /// 782438: 36 at 0.416666 net is 17.33 in SAP, and 17.28 on a receipt that charges each unit 0.48. That
    /// is till rounding, which the reconciliation does not flag, so neither does this.
    /// </summary>
    [Fact]
    public void Agrees_AcceptsTillRoundingTheReconciliationAccepts()
    {
        var invoice = SapInvoice(docTotal: 17.33m, (36m, 0.481249m, 15.00m));

        Assert.Equal(17.28m, FiscalReceiptAmountCheck.RepricedAtReceiptPrices(invoice));
        Assert.True(FiscalReceiptAmountCheck.Agrees(17.28m, invoice.DocTotal, 17.28m));
        Assert.True(FiscalReceiptAmountCheck.Agrees(17.33m, invoice.DocTotal, 17.28m));
    }

    [Fact]
    public void RepricedAtReceiptPrices_IsNullWhenSapSentALineWithoutItsGross()
    {
        var invoice = SapInvoice(docTotal: 8.70m, (10m, 0m, 7.53m));

        Assert.Null(FiscalReceiptAmountCheck.RepricedAtReceiptPrices(invoice));
        Assert.False(FiscalReceiptAmountCheck.Agrees(7.50m, invoice.DocTotal, null));
    }

    /// <summary>
    /// The bug itself, refused before anything is signed: a pre-SAP line with its net price and no gross.
    /// The platform client is a stub that throws on any call, so a submission would fail the test.
    /// </summary>
    [Fact]
    public async Task FiscalizePreSapInvoiceAsync_RefusesALineWithoutItsGrossPriceAndSignsNothing()
    {
        var service = new FiscalizationService(
            StubProxy.Unused<IFiscalisationApiClient>(),
            StubProxy.Unused<IFiscalDeviceConfigCache>(),
            Options.Create(new FiscalisationSettings { Enabled = true, DefaultDeviceId = 46668 }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance);

        var result = await service.FiscalizePreSapInvoiceAsync(
            new InvoiceDto
            {
                DocCurrency = "USD",
                DocTotal = 7.53m,
                Lines =
                [
                    new InvoiceLineDto
                    {
                        LineNum = 0,
                        ItemCode = "VHU002",
                        Quantity = 10m,
                        UnitPrice = 0.7532m,
                        TaxCode = "O01"
                    }
                ]
            },
            "VAN005-INV-20261005-A64C0F");

        Assert.False(result.Success);
        Assert.Equal(FiscalizationService.GrossPriceMissingErrorCode, result.ErrorCode);
    }

    [Fact]
    public void FindLineWithoutGrossPrice_PassesAFreeLineAndAGrossedUpOne()
    {
        var invoice = new InvoiceDto
        {
            Lines =
            [
                new InvoiceLineDto { LineNum = 0, ItemCode = "FREE", Quantity = 1m, UnitPrice = 0m, GrossPrice = 0m },
                new InvoiceLineDto { LineNum = 1, ItemCode = "VHU002", Quantity = 10m, UnitPrice = 0.7532m, GrossPrice = 0.87m }
            ]
        };

        Assert.Null(FiscalizationService.FindLineWithoutGrossPrice(invoice));
    }

    private static InvoiceDto SapInvoice(
        decimal docTotal,
        params (decimal Quantity, decimal PriceAfterVat, decimal LineTotal)[] lines)
        => new()
        {
            DocNum = 784863,
            DocTotal = docTotal,
            DocCurrency = "USD",
            Lines = lines.Select((line, index) => new InvoiceLineDto
            {
                LineNum = index,
                ItemCode = "VHU002",
                Quantity = line.Quantity,
                PriceAfterVat = line.PriceAfterVat,
                LineTotal = line.LineTotal
            }).ToList()
        };
}
