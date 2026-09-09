using ShopInventory.DTOs;
using ShopInventory.Features.SalesOrders.Commands.CreateSalesOrder;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Every sales order create has to arrive with a key, whatever its source.
/// </summary>
/// <remarks>
/// <see cref="ShopInventory.Middleware.IdempotencyMiddleware"/> refuses a keyless
/// <c>POST /api/salesorder</c> with 428 — except for the merchandiser, sales rep, ADR and sales
/// roles, whose older mobile clients send the key in the body rather than the header. That exemption
/// is granted by <b>role</b>, and the rule that made it safe was written by <b>source</b>: one of
/// those users posting an order that was not <c>Source=Mobile</c> passed the gate carrying no key at
/// all, and <c>SalesOrderService.CreateAsync</c> then had nothing to deduplicate on. A resubmission
/// became a second order, and a second SAP document once it was approved.
///
/// The controller folds an <c>Idempotency-Key</c> header into <c>ClientRequestId</c> before
/// validation runs, so a caller supplying either one passes.
/// </remarks>
public sealed class SalesOrderKeyRequirementTests
{
    [Theory]
    [InlineData(SalesOrderSource.Mobile)]
    [InlineData(SalesOrderSource.Web)]
    [InlineData(null)]
    public void A_create_without_a_key_is_refused_whatever_its_source(SalesOrderSource? source)
    {
        var result = Validate(clientRequestId: null, source);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.PropertyName.EndsWith("ClientRequestId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SalesOrderSource.Mobile)]
    [InlineData(SalesOrderSource.Web)]
    [InlineData(null)]
    public void A_create_carrying_a_key_is_accepted(SalesOrderSource? source)
    {
        var result = Validate(clientRequestId: "9a7c4e1b2d3f5a6b8c0d1e2f3a4b5c6d", source);

        Assert.DoesNotContain(
            result.Errors,
            error => error.PropertyName.EndsWith("ClientRequestId", StringComparison.Ordinal));
    }

    /// <summary>
    /// The message has to name both ways of supplying it, because which one a client uses depends on
    /// how old it is, and the caller reading this cannot see the middleware that let it through.
    /// </summary>
    [Fact]
    public void The_refusal_names_both_ways_to_supply_the_key()
    {
        var message = Assert.Single(
            Validate(clientRequestId: null, SalesOrderSource.Web).Errors,
            error => error.PropertyName.EndsWith("ClientRequestId", StringComparison.Ordinal))
            .ErrorMessage;

        Assert.Contains("clientRequestId", message, StringComparison.Ordinal);
        Assert.Contains("Idempotency-Key", message, StringComparison.Ordinal);
    }

    private static FluentValidation.Results.ValidationResult Validate(
        string? clientRequestId,
        SalesOrderSource? source)
    {
        var request = new CreateSalesOrderRequest
        {
            CardCode = "ABS006",
            CardName = "Absolute Traders",
            ClientRequestId = clientRequestId,
            Lines =
            [
                new CreateSalesOrderLineRequest
                {
                    ItemCode = "NRI049",
                    Quantity = 4m,
                    UnitPrice = 60.00m
                }
            ]
        };

        if (source.HasValue)
        {
            request.Source = source.Value;
        }

        return new CreateSalesOrderValidator().Validate(
            new CreateSalesOrderCommand(request, Guid.NewGuid()));
    }
}
