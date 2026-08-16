using System.Text.Json;
using ShopInventory.Common.Sales;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Pins who a till is allowed to sell as, and from where.
///
/// POST /api/DesktopIntegration/sales used to take the customer and the warehouse from the request
/// body and check neither against the account that signed in, so any authenticated till could invoice
/// any business partner and deduct from any warehouse's snapshot. Both now come from the account.
///
/// The assertions worth keeping are the ones about the LINE warehouses: locking, stock validation and
/// the snapshot deduction all key on the line, so a fix that only rewrote the header would look
/// correct on the saved row while leaving the stock movement exactly as attacker-controlled as before.
/// </summary>
public sealed class SellingAccountTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static User Account(
        string? businessPartner = "KEFSHOP-BP",
        string?[]? warehouses = null,
        string? costCentre = "CC-01",
        bool isActive = true) => new()
        {
            Id = UserId,
            Username = "till01",
            PasswordHash = "not-used-here",
            IsActive = isActive,
            Role = "Cashier",
            AssignedBusinessPartnerCode = businessPartner,
            AssignedCostCentreCode = costCentre,
            AssignedWarehouseCodes = warehouses is null
                ? JsonSerializer.Serialize(new[] { "KEFSHOP" })
                : JsonSerializer.Serialize(warehouses),
        };

    private static CreateDesktopSaleRequest Sale(
        string? cardCode = null,
        string? warehouse = null,
        params string?[] lineWarehouses)
    {
        var lines = (lineWarehouses.Length == 0 ? [null] : lineWarehouses)
            .Select((wh, i) => new CreateDesktopSaleLineRequest
            {
                LineNum = i + 1,
                ItemCode = $"ITEM-{i + 1}",
                Quantity = 1,
                UnitPrice = 10m,
                WarehouseCode = wh ?? string.Empty,
            })
            .ToList();

        return new CreateDesktopSaleRequest
        {
            CardCode = cardCode ?? string.Empty,
            WarehouseCode = warehouse ?? string.Empty,
            Lines = lines,
        };
    }

    // ---- Resolving the account ------------------------------------------------------------------

    [Fact]
    public void A_configured_account_resolves_to_its_own_customer_warehouse_and_cost_centre()
    {
        var resolved = SellingAccountResolver.Resolve(Account());

        Assert.False(resolved.IsError);
        Assert.Equal(UserId, resolved.Value.UserId);
        Assert.Equal("KEFSHOP-BP", resolved.Value.CardCode);
        Assert.Equal("KEFSHOP", resolved.Value.WarehouseCode);
        Assert.Equal("CC-01", resolved.Value.CostCentreCode);
    }

    [Fact]
    public void An_account_with_no_business_partner_cannot_sell()
    {
        var resolved = SellingAccountResolver.Resolve(Account(businessPartner: null));

        Assert.True(resolved.IsError);
        Assert.Equal("DesktopSales.MissingCustomerAssignment", resolved.FirstError.Code);
    }

    [Fact]
    public void An_account_with_no_warehouse_cannot_sell()
    {
        var resolved = SellingAccountResolver.Resolve(Account(warehouses: []));

        Assert.True(resolved.IsError);
        Assert.Equal("DesktopSales.MissingWarehouseAssignment", resolved.FirstError.Code);
    }

    [Fact]
    public void Two_assigned_warehouses_are_refused_rather_than_picked_between()
    {
        // A business partner draws from exactly one warehouse. Taking the first entry would sell from
        // whichever the JSON array happened to list first, silently and differently per account.
        var resolved = SellingAccountResolver.Resolve(Account(warehouses: ["KEFSHOP", "KEFGRS"]));

        Assert.True(resolved.IsError);
        Assert.Equal("DesktopSales.AmbiguousWarehouseAssignment", resolved.FirstError.Code);
    }

    [Fact]
    public void The_same_warehouse_listed_twice_is_not_ambiguous()
    {
        var resolved = SellingAccountResolver.Resolve(Account(warehouses: ["KEFSHOP", "kefshop"]));

        Assert.False(resolved.IsError);
        Assert.Equal("KEFSHOP", resolved.Value.WarehouseCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_cost_centre_does_not_stop_a_sale(string? costCentre)
    {
        // Deliberately softer than the customer and warehouse: SAP defaults a missing cost centre, so
        // requiring it would stop an otherwise correctly configured till from trading for nothing that
        // affects the money.
        var resolved = SellingAccountResolver.Resolve(Account(costCentre: costCentre));

        Assert.False(resolved.IsError);
        Assert.Null(resolved.Value.CostCentreCode);
    }

    [Fact]
    public void A_deactivated_account_cannot_sell()
    {
        var resolved = SellingAccountResolver.Resolve(Account(isActive: false));

        Assert.True(resolved.IsError);
        Assert.Equal("DesktopSales.Unauthenticated", resolved.FirstError.Code);
    }

    [Fact]
    public void An_unknown_user_cannot_sell()
    {
        // What the handler passes when the token names a user that is not in the table.
        var resolved = SellingAccountResolver.Resolve(null);

        Assert.True(resolved.IsError);
        Assert.Equal("DesktopSales.Unauthenticated", resolved.FirstError.Code);
    }

    // ---- Applying it to the request ---------------------------------------------------------------

    private static SellingAccountAssignments Assignments =>
        new(UserId, "KEFSHOP-BP", "KEFSHOP", "CC-01");

    [Fact]
    public void A_request_naming_nothing_is_filled_in_from_the_account()
    {
        // The normal case for a migrated till: it sends a basket and nothing about identity.
        var req = Sale();

        var error = CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        Assert.Null(error);
        Assert.Equal("KEFSHOP-BP", req.CardCode);
        Assert.Equal("KEFSHOP", req.WarehouseCode);
        Assert.All(req.Lines, l => Assert.Equal("KEFSHOP", l.WarehouseCode));
    }

    [Fact]
    public void Every_line_is_pointed_at_the_accounts_warehouse()
    {
        // THE security assertion. Stock is deducted per line, so a line naming another warehouse is
        // the actual attack, not the header.
        var req = Sale(lineWarehouses: ["KEFGRS", "CORMACH2", null]);

        var error = CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        // Refused outright rather than quietly corrected — but if this ever becomes a rewrite, the
        // second assertion is the one that must survive.
        Assert.NotNull(error);
        Assert.Equal("DesktopSales.AssignmentMismatch", error.Value.Code);
    }

    [Fact]
    public void A_line_warehouse_that_agrees_with_the_account_is_accepted_and_normalised()
    {
        var req = Sale(warehouse: "kefshop", lineWarehouses: ["KEFSHOP", "kefshop"]);

        var error = CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        Assert.Null(error);
        Assert.Equal("KEFSHOP", req.WarehouseCode);
        Assert.All(req.Lines, l => Assert.Equal("KEFSHOP", l.WarehouseCode));
    }

    [Fact]
    public void A_request_naming_another_customer_is_refused()
    {
        var req = Sale(cardCode: "SOMEONE-ELSE");

        var error = CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        Assert.NotNull(error);
        Assert.Equal("DesktopSales.AssignmentMismatch", error.Value.Code);
        Assert.Contains("SOMEONE-ELSE", error.Value.Description);
    }

    [Fact]
    public void A_request_naming_another_warehouse_is_refused()
    {
        var req = Sale(warehouse: "KEFGRS");

        var error = CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        Assert.NotNull(error);
        Assert.Equal("DesktopSales.AssignmentMismatch", error.Value.Code);
    }

    [Fact]
    public void A_matching_customer_differing_only_in_case_is_accepted()
    {
        var req = Sale(cardCode: "kefshop-bp");

        var error = CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        Assert.Null(error);
        Assert.Equal("KEFSHOP-BP", req.CardCode);
    }

    [Fact]
    public void Nothing_is_rewritten_when_the_request_is_refused()
    {
        // The handler returns before saving, but if that ever changes the request must not have been
        // half-applied on the way out.
        var req = Sale(cardCode: "SOMEONE-ELSE", warehouse: "KEFGRS");

        CreateDesktopSaleHandler.ApplyAccountToRequest(req, Assignments);

        Assert.Equal("SOMEONE-ELSE", req.CardCode);
        Assert.Equal("KEFGRS", req.WarehouseCode);
    }
}
