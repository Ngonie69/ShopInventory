using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// SAP invoice 784863, VAN005-INV-20261005-A64C0F: ten Vhuka Vhuka 80g at 0.7532 net under O01, which SAP
/// invoiced at 8.70. Signed before the queue job grossed its prices up, the receipt declared the net price
/// as the gross — 10 x 0.75 = 7.50 — understating the sale by 1.20 and its VAT by all of it.
/// </summary>
public sealed class QueuedInvoiceReceiptPricingTests
{
    [Fact]
    public void BuildInvoiceDtoFromPayload_FilesTheConvertedVanOrderAtWhatSapInvoiced()
    {
        var request = new CreateStockReservationRequest
        {
            ExternalReference = "VAN005-INV-20261005-A64C0F",
            CardCode = "VAN009",
            Currency = "USD",
            Lines =
            [
                new CreateStockReservationLineRequest
                {
                    LineNum = 0,
                    ItemCode = "VHU002",
                    ItemDescription = "Vhuka Vhuka 80g",
                    Quantity = 10m,
                    UnitPrice = 0.7532m,
                    WarehouseCode = "VAN009"
                }
            ]
        };

        var dto = InvoicePostingJob.BuildInvoiceDtoFromPayload(
            new InvoiceQueueEntity { ExternalReference = request.ExternalReference, Currency = "USD" },
            request,
            new TaxSettings { VatRate = 0.155m },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["VHU002"] = "O01" });

        var line = Assert.Single(dto.Lines!);
        Assert.Equal(0.87m, line.GrossPrice);
        Assert.Equal("O01", line.TaxCode);
        Assert.Equal(8.70m, dto.DocTotal);
        Assert.Equal(1.17m, dto.VatSum);
    }
}
