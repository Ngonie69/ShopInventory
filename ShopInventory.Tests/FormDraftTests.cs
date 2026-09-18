using System.Security.Claims;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the drafts the create forms keep in the browser, so a reload after the circuit is lost
/// brings the operator's entries back. See <see cref="FormDraft"/>.
/// </summary>
public sealed class FormDraftTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    private static InvoiceDraft Invoice() => new()
    {
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

    private static string Stored(DateTimeOffset savedAt) => FormDraft.Serialize(Invoice(), savedAt);

    [Fact]
    public void RoundTrip_KeepsLinesCustomerDefaultsAndIdempotencyKey()
    {
        var restored = FormDraft.TryRestore<InvoiceDraft>(Stored(Now.AddMinutes(-3)), Now, FormDraft.DefaultMaxAge);

        Assert.NotNull(restored);
        Assert.Equal(Now.AddMinutes(-3), restored.SavedAt);
        var draft = restored.State;
        Assert.Equal("CIS006", draft.Invoice.CardCode);
        Assert.Equal("abc123", draft.Invoice.ClientRequestId);
        Assert.Equal("KEFSHOP", draft.WarehouseCode);
        Assert.Equal("CC01", draft.DefaultCostCentre);
        Assert.False(draft.NotifyCustomerByEmail);

        var line = Assert.Single(draft.Invoice.Lines);
        Assert.Equal("CHE011", line.ItemCode);
        Assert.Equal(2.5m, line.Quantity);
        Assert.Equal(12.34m, line.UnitPrice);
        Assert.Equal(5m, line.DiscountPercent);
        Assert.Equal("KG", line.UoMCode);
    }

    [Fact]
    public void TryRestore_DropsADraftOlderThanAShift()
    {
        var json = Stored(Now - FormDraft.DefaultMaxAge - TimeSpan.FromMinutes(1));

        Assert.Null(FormDraft.TryRestore<InvoiceDraft>(json, Now, FormDraft.DefaultMaxAge));
    }

    [Fact]
    public void TryRestore_KeepsADraftJustInsideTheWindow()
    {
        var json = Stored(Now - FormDraft.DefaultMaxAge + TimeSpan.FromMinutes(1));

        Assert.NotNull(FormDraft.TryRestore<InvoiceDraft>(json, Now, FormDraft.DefaultMaxAge));
    }

    [Fact]
    public void TryRestore_RefusesADraftStampedInTheFuture()
    {
        Assert.Null(FormDraft.TryRestore<InvoiceDraft>(Stored(Now.AddHours(1)), Now, FormDraft.DefaultMaxAge));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"savedAt\":\"2026-09-18T10:00:00Z\",\"state\":null}")]
    [InlineData("[]")]
    [InlineData("{\"savedAt\":\"2026-09-18T10:00:00Z\",\"state\":{\"invoice\":{\"lines\":\"x\"}}}")]
    public void TryRestore_IgnoresMissingOrUnreadableValues(string? json)
    {
        Assert.Null(FormDraft.TryRestore<InvoiceDraft>(json, Now, FormDraft.DefaultMaxAge));
    }

    [Fact]
    public void InvoiceIsEmpty_IsFalseOnceAnythingIsEntered()
    {
        Assert.True(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { DocCurrency = "ZIG" }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { CardCode = "CIS006" }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { Comments = "deliver pm" }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { CrateQuantity = 0 }));
        Assert.False(InvoiceDraft.IsEmpty(new CreateInvoiceRequest { Lines = { new CreateInvoiceLineRequest() } }));
    }

    [Fact]
    public void Fingerprint_IsStableAndSeesEdits()
    {
        var first = Invoice();
        var second = Invoice();
        Assert.Equal(FormDraft.Fingerprint(first), FormDraft.Fingerprint(second));

        second.Invoice.Lines[0].Quantity = 3;
        Assert.NotEqual(FormDraft.Fingerprint(first), FormDraft.Fingerprint(second));
    }

    [Fact]
    public void StorageKey_IsPerFormAndPerUserAndNeedsAUser()
    {
        Assert.Equal(FormDraft.StorageKey("invoice", "Tendai"), FormDraft.StorageKey("invoice", " tendai "));
        Assert.NotEqual(FormDraft.StorageKey("invoice", "tendai"), FormDraft.StorageKey("invoice", "rudo"));
        Assert.NotEqual(FormDraft.StorageKey("invoice", "tendai"), FormDraft.StorageKey("sales-order", "tendai"));
        Assert.Null(FormDraft.StorageKey("invoice", null));
        Assert.Null(FormDraft.StorageKey("invoice", "  "));
    }

    [Fact]
    public void UserName_IsOnlyForASignedInUser()
    {
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tendai") }, "jwt"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "tendai") }));

        Assert.Equal("tendai", FormDraft.UserName(signedIn));
        Assert.Null(FormDraft.UserName(anonymous));
        Assert.Null(FormDraft.UserName(null));
    }

    [Theory]
    [InlineData(0, "Restored the payment you were entering (last changed")]
    [InlineData(1, "Restored the payment you were entering (1 line, last changed")]
    [InlineData(3, "Restored the payment you were entering (3 lines, last changed")]
    public void RestoredNotice_CountsLines(int lines, string expectedStart)
    {
        Assert.StartsWith(expectedStart, FormDraft.RestoredNotice("payment", lines, Now));
    }
}
