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

    /// <summary>
    /// Longer than a person will wait. The budget is moved past on <see cref="_clock"/>, so this is a
    /// claim about the handler's budget rather than about how fast the machine running the suite is.
    /// </summary>
    private static readonly TimeSpan PatienceLimit = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Only a hang fails on this: everything these tests wait for is signalled, never timed.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);

    private readonly CapturingLogger<GetPricesByBusinessPartnerHandler> _log = new();

    /// <summary>The handler's clock. Its budget cannot run out until a test moves this.</summary>
    private readonly ManualClock _clock = new();

    /// <summary>Set once a held SAP call has been entered, so the request is waiting on SAP.</summary>
    private readonly TaskCompletionSource _heldInSap = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task A_slow_SAP_falls_back_to_the_catalogue_rather_than_holding_the_request()
    {
        var handler = CreateHandler(sapAnswers: false);

        var answer = handler.Handle(NewQuery(), CancellationToken.None);
        await _heldInSap.Task.WaitAsync(HangGuard);

        // SAP never answers, so the budget is the only way this request can end.
        _clock.Advance(PatienceLimit);
        var result = await answer.WaitAsync(HangGuard);

        Assert.False(result.IsError);
        Assert.Equal(12.30m, Assert.Single(result.Value.Prices).Price);

        Assert.Contains(
            _log.Entries,
            entry => entry.Message.Contains("did not answer within") && entry.Message.Contains(CardCode));
    }

    /// <summary>Giving up on SAP is not a fault — it is the design working.</summary>
    [Fact]
    public async Task Falling_back_on_the_budget_is_not_logged_as_an_error()
    {
        var handler = CreateHandler(sapAnswers: false);

        var answer = handler.Handle(NewQuery(), CancellationToken.None);
        await _heldInSap.Task.WaitAsync(HangGuard);
        _clock.Advance(PatienceLimit);
        await answer.WaitAsync(HangGuard);

        Assert.Contains(_log.Entries, entry => entry.Message.Contains("did not answer within"));
        Assert.DoesNotContain(_log.AtOrAbove(LogLevel.Error), entry => entry.Message.Contains("did not answer"));
    }

    [Fact]
    public async Task A_healthy_SAP_still_answers_with_live_prices()
    {
        var handler = CreateHandler(sapAnswers: true);

        var result = await handler.Handle(NewQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(9, result.Value.PriceListNum);
        Assert.Equal(12.34m, Assert.Single(result.Value.Prices).Price);
        Assert.Contains(_log.Entries, entry => entry.Message.Contains("live SAP item prices"));
    }

    /// <summary>
    /// The caller hanging up is a different thing from the budget expiring, and must still surface
    /// as a cancellation rather than being answered from the catalogue.
    /// </summary>
    [Fact]
    public async Task A_caller_that_hangs_up_is_not_answered_from_the_catalogue()
    {
        var handler = CreateHandler(sapAnswers: false);
        using var caller = new CancellationTokenSource();

        var answer = handler.Handle(NewQuery(), caller.Token);
        await _heldInSap.Task.WaitAsync(HangGuard);

        // Hung up while SAP is still being waited on. The handler's clock is never moved, so its
        // budget cannot have run out: the only cancellation it sees is the caller's.
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer.WaitAsync(HangGuard));
        Assert.DoesNotContain(_log.Entries, entry => entry.Message.Contains("locally stored prices"));
    }

    private static GetPricesByBusinessPartnerQuery NewQuery() =>
        new(CardCode, ForceRefresh: false, ItemCodes: null, UseLivePricing: true);

    /// <param name="sapAnswers">
    /// Whether SAP answers at once, or holds every price call until it is cancelled — a hung Service
    /// Layer, which no wall-clock delay can stand in for without racing the budget.
    /// </param>
    private GetPricesByBusinessPartnerHandler CreateHandler(bool sapAnswers)
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
                AnswerOrHold(sapAnswers, new List<ItemPriceByListDto>
                {
                    new() { ItemCode = "ITEM-1", Price = 12.34m }
                }, LastToken(args)),

            nameof(ISAPServiceLayerClient.GetSpecialPricesForBPAsync) =>
                AnswerOrHold(sapAnswers, new Dictionary<string, decimal>(), LastToken(args)),

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

        return new GetPricesByBusinessPartnerHandler(catalogue, sap, _log, _clock);
    }

    private static CancellationToken LastToken(object?[]? args)
        => args?.OfType<CancellationToken>().LastOrDefault() ?? CancellationToken.None;

    private async Task<T> AnswerOrHold<T>(bool sapAnswers, T value, CancellationToken cancellationToken)
    {
        if (!sapAnswers)
        {
            _heldInSap.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return value;
    }
}
