using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The totals a van sales handset is sent for a document.
/// </summary>
/// <remarks>
/// <para>Only the net used to go out, and the handset added the tax back itself. That was wrong twice.
/// The net is rounded to the cent on the way — <c>SalesOrderLineEntity.LineTotal</c> is
/// <c>decimal(18,2)</c> where <c>UnitPrice</c> is <c>decimal(18,4)</c> — so taxing it can cross a
/// rounding midpoint the true net does not, and a real order came out a cent over on the handset's own
/// list. And the rate it taxed at is a fallback constant, used whenever the device holds no fiscal
/// lease, which a van not registered as a fiscal device never does.</para>
///
/// <para>Both are answered by sending the total the server already holds. The net stays where it was,
/// because it is what every other reader of this DTO has always been given.</para>
/// </remarks>
public sealed class VanSalesLegacyTotalTests
{
    [Fact]
    public void A_sales_order_carries_the_total_it_is_billed_at()
    {
        var mapped = VanSalesCompatibilityMapper.MapLegacySalesOrder(new SalesOrderDto
        {
            Id = 3442,
            OrderNumber = "SO-3442",
            CardCode = "C-VAN-014",
            DocTotal = 0.63m,
            TaxAmount = 0.08m
        });

        Assert.Equal(0.63, mapped.Gross);

        // The net is unchanged, and it is not the same number. A reader that wants one must not be
        // handed the other.
        Assert.Equal(0.55, mapped.Price);
    }

    [Fact]
    public void An_invoice_carries_it_too()
    {
        // The invoice list derived its totals exactly as the sales order list did, so it carried the
        // same cent and the same guessed rate.
        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(
            new Invoice
            {
                DocEntry = 9001,
                DocNum = 4021,
                CardCode = "C-VAN-014",
                DocDate = "2026-08-20",
                DocDueDate = "2026-08-20",
                DocCurrency = "USD",
                DocTotal = 118m,
                VatSum = 18m,
                DocumentLines = []
            },
            fiscalTransaction: null);

        Assert.Equal(118d, mapped.Gross);
        Assert.Equal(100d, mapped.Price);
    }

    [Fact]
    public void A_document_that_is_all_tax_free_reports_the_same_figure_twice()
    {
        // Net and gross agree when nothing was taxed, which is a real state rather than a missing one.
        // Worth pinning because the handset reads a *missing* gross as "derive it yourself", and these
        // two must not be confused: this document is genuinely worth what it says.
        var mapped = VanSalesCompatibilityMapper.MapLegacySalesOrder(new SalesOrderDto
        {
            Id = 7,
            OrderNumber = "SO-7",
            CardCode = "C-VAN-014",
            DocTotal = 40m,
            TaxAmount = 0m
        });

        Assert.Equal(40d, mapped.Gross);
        Assert.Equal(40d, mapped.Price);
    }
}
