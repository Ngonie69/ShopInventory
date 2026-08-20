using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ShopInventory.DTOs;
using ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that a person waiting on business-partner prices is not made to wait on SAP indefinitely.
/// </summary>
/// <remarks>
/// Asked without item codes, this query used to fall onto the whole-catalogue price list path, whose
/// budget is 20 seconds — sized for the four-hourly sync — with an Items API fallback behind it that
/// has taken two minutes for a single list. On 2026-08-20 five of these ran between 08:06 and 08:10;
/// the load tipped SAP into <c>BadGateway</c> and took the system to Degraded for five minutes.
/// <para>
/// The catalogue answer already existed as the failure path. What was missing was giving up in time
/// to use it.
/// </para>
/// </remarks>
public sealed class LivePricingBudgetTests
{
    private const string CardCode = "SPA077";

    private readonly CapturingLogger<GetPricesByBusinessPartnerHandler> _log = new();

    [Fact]
    public async Task A_slow_SAP_falls_back_to_the_catalogue_rather_than_holding_the_request()
    {
        // Far longer than the budget, and longer than a person will wait.
        var handler = CreateHandler(sapDelay: TimeSpan.FromSeconds(30));

        var timer = Stopwatch.StartNew();
        var result = await handler.Handle(NewQuery(), CancellationToken.None);
        timer.Stop();

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.TotalCount);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(15),
            $"Expected the budget to cut this short; it took {timer.Elapsed.TotalSeconds:F1}s.");

        Assert.Contains(
            _log.Entries,
            entry => entry.Message.Contains("did not answer within") && entry.Message.Contains(CardCode));
    }

    /// <summary>Giving up on SAP is not a fault — it is the design working.</summary>
    [Fact]
    public async Task Falling_back_on_the_budget_is_not_logged_as_an_error()
    {
        var handler = CreateHandler(sapDelay: TimeSpan.FromSeconds(30));

        await handler.Handle(NewQuery(), CancellationToken.None);

        Assert.DoesNotContain(_log.AtOrAbove(LogLevel.Error), entry => entry.Message.Contains("did not answer"));
    }

    [Fact]
    public async Task A_healthy_SAP_still_answers_with_live_prices()
    {
        var handler = CreateHandler(sapDelay: TimeSpan.Zero);

        var result = await handler.Handle(NewQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(9, result.Value.PriceListNum);
        Assert.Contains(_log.Entries, entry => entry.Message.Contains("live SAP item prices"));
    }

    /// <summary>
    /// The caller hanging up is a different thing from the budget expiring, and must still surface
    /// as a cancellation rather than being answered from the catalogue.
    /// </summary>
    [Fact]
    public async Task A_caller_that_hangs_up_is_not_answered_from_the_catalogue()
    {
        var handler = CreateHandler(sapDelay: TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.Handle(NewQuery(), caller.Token));
    }

    private static GetPricesByBusinessPartnerQuery NewQuery() =>
        new(CardCode, ForceRefresh: false, ItemCodes: null, UseLivePricing: true);

    private GetPricesByBusinessPartnerHandler CreateHandler(TimeSpan sapDelay)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetBusinessPartnerByCodeAsync) =>
                Task.FromResult<BusinessPartnerDto?>(new BusinessPartnerDto
                {
                    CardCode = CardCode,
                    PriceListNum = 9,
                    Currency = "USD"
                }),

            // The whole-catalogue path: this is the call that takes 20s+ in production.
            nameof(ISAPServiceLayerClient.GetPricesByPriceListAsync) =>
                DelayThen(sapDelay, new List<ItemPriceByListDto>
                {
                    new() { ItemCode = "ITEM-1", Price = 12.34m }
                }, LastToken(args)),

            nameof(ISAPServiceLayerClient.GetSpecialPricesForBPAsync) =>
                DelayThen(sapDelay, new Dictionary<string, decimal>(), LastToken(args)),

            _ => throw new InvalidOperationException($"Unexpected call to {method.Name}")
        });

        var catalogue = StubProxy.For<ILocalPriceCatalogService>((method, _) =>
            method.Name == nameof(ILocalPriceCatalogService.GetBusinessPartnerPricingAsync)
                ? Task.FromResult<LocalBusinessPartnerPricingResult?>(new LocalBusinessPartnerPricingResult
                {
                    BusinessPartner = new BusinessPartnerDto { CardCode = CardCode },
                    Prices = new ItemPricesByListResponseDto
                    {
                        TotalCount = 1,
                        PriceListNum = 9,
                        Prices = [new ItemPriceByListDto { ItemCode = "ITEM-1", Price = 12.30m }]
                    }
                })
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        return new GetPricesByBusinessPartnerHandler(catalogue, sap, _log);
    }

    private static CancellationToken LastToken(object?[]? args)
        => args?.OfType<CancellationToken>().LastOrDefault() ?? CancellationToken.None;

    private static async Task<T> DelayThen<T>(TimeSpan delay, T value, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return value;
    }
}
