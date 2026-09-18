using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the invoice draft the Create Invoice page keeps in the browser, so a reload after the
/// circuit is lost brings the cashier's lines back.
/// </summary>
public sealed class InvoiceDraftTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    private static InvoiceDraft Draft(DateTimeOffset savedAt) => new()
    {
        SavedAt = savedAt,
        WarehouseCode = "KEFSHOP",
        DefaultCostCentre = "CC01",
        NotifyCustomerByEmail = false,
        Invoice = new CreateInvoiceRequest
        {
            CardCode = "CIS006",
            DocCurrency = "USD",
            ClientRequestId = "abc123",
            Lines =
            {
                new CreateInvoiceLineRequest
                {
                    ItemCode = "CHE011",
                    ItemDescription = "Cheddar",
                    Quantity = 2.5m,
                    UnitPrice = 12.34m,
                    DiscountPercent = 5m,
                    WarehouseCode = "KEFSHOP",
                    UoMCode = "KG"
                }
            }
        }
    };

    [Fact]
    public void RoundTrip_KeepsLinesCustomerDefaultsAndIdempotencyKey()
    {
        var restored = InvoiceDraft.TryRestore(Draft(Now.AddMinutes(-3)).Serialize(), Now);

        Assert.NotNull(restored);
        Assert.Equal("CIS006", restored.Invoice.CardCode);
        Assert.Equal("abc123", restored.Invoice.ClientRequestId);
        Assert.Equal("KEFSHOP", restored.WarehouseCode);
        Assert.Equal("CC01", restored.DefaultCostCentre);
        Assert.False(restored.NotifyCustomerByEmail);

        var line = Assert.Single(restored.Invoice.Lines);
        Assert.Equal("CHE011", line.ItemCode);
        Assert.Equal(2.5m, line.Quantity);
        Assert.Equal(12.34m, line.UnitPrice);
        Assert.Equal(5m, line.DiscountPercent);
        Assert.Equal("KG", line.UoMCode);
    }

    [Fact]
    public void TryRestore_DropsADraftOlderThanAShift()
    {
        var json = Draft(Now - InvoiceDraft.MaxAge - TimeSpan.FromMinutes(1)).Serialize();

        Assert.Null(InvoiceDraft.TryRestore(json, Now));
    }

    [Fact]
    public void TryRestore_KeepsADraftJustInsideTheWindow()
    {
        var json = Draft(Now - InvoiceDraft.MaxAge + TimeSpan.FromMinutes(1)).Serialize();

        Assert.NotNull(InvoiceDraft.TryRestore(json, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"invoice\":null}")]
    [InlineData("[]")]
    public void TryRestore_IgnoresMissingOrUnreadableValues(string? json)
    {
        Assert.Null(InvoiceDraft.TryRestore(json, Now));
    }

    [Fact]
    public void TryRestore_IgnoresAnEmptyForm()
    {
        var draft = new InvoiceDraft { SavedAt = Now, Invoice = new CreateInvoiceRequest { DocCurrency = "USD" } };

        Assert.Null(InvoiceDraft.TryRestore(draft.Serialize(), Now));
    }

    [Fact]
    public void IsEmpty_IsFalseOnceAnythingIsEntered()
    {
        Assert.True(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { DocCurrency = "ZIG" }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { CardCode = "CIS006" }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { Comments = "deliver pm" }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { Lines = { new CreateInvoiceLineRequest() } }));
    }

    [Fact]
    public void Fingerprint_IgnoresSaveTimeButSeesEdits()
    {
        var first = Draft(Now);
        var later = Draft(Now.AddMinutes(10));
        Assert.Equal(first.Fingerprint(), later.Fingerprint());

        later.Invoice.Lines[0].Quantity = 3;
        Assert.NotEqual(first.Fingerprint(), later.Fingerprint());
    }

    [Fact]
    public void StorageKey_IsPerUserAndNeedsOne()
    {
        Assert.Equal(InvoiceDraft.StorageKey("Tendai"), InvoiceDraft.StorageKey(" tendai "));
        Assert.NotEqual(InvoiceDraft.StorageKey("tendai"), InvoiceDraft.StorageKey("rudo"));
        Assert.Null(InvoiceDraft.StorageKey(null));
        Assert.Null(InvoiceDraft.StorageKey("  "));
    }
}
