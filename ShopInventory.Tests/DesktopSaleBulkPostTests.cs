using ErrorOr;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Errors;
using ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;
using ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSalesToSap;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what a bulk post does with a set of sales that end differently.
///
/// The batch is not a transaction and could not be one: each sale becomes its own SAP document, and
/// SAP cannot unwind the first four when the fifth is refused. So the contract is a row per sale, and
/// the failures worth guarding against are the two ways that contract could be broken — one refused
/// sale stopping the rest, and one refused sale being reported as if it had posted.
/// </summary>
public sealed class DesktopSaleBulkPostTests
{
    private static readonly Guid Caller = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task One_refused_sale_does_not_stop_the_rest()
    {
        var handler = Handler(reference => reference switch
        {
            "B" => Errors.DesktopSales.SalePostFailed("B", "item is blocked for sale"),
            _ => Posted(reference)
        });

        var result = await handler.Handle(Command("A", "B", "C"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(3, result.Value.Requested);
        Assert.Equal(2, result.Value.Posted);
        Assert.Equal(1, result.Value.Failed);

        // The refusal reaches the operator as its own row, carrying SAP's words. A batch that
        // reported only "2 of 3 posted" would name neither the sale nor the reason.
        var refused = result.Value.Results.Single(row => row.ExternalReferenceId == "B");
        Assert.Equal(DesktopSalePostOutcomes.Failed, refused.Outcome);
        Assert.Equal("item is blocked for sale", refused.Message);
    }

    [Fact]
    public async Task A_sale_another_post_is_already_handling_is_reported_apart_from_a_failure()
    {
        // Nothing went wrong and nothing was written; whoever holds the claim is finishing the job.
        // Folding this into "failed" would send somebody chasing a sale that is about to post.
        var handler = Handler(reference => reference switch
        {
            "B" => Errors.DesktopSales.SalePostInProgress("B"),
            _ => Posted(reference)
        });

        var result = await handler.Handle(Command("A", "B"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.InProgress);
        Assert.Equal(0, result.Value.Failed);
    }

    [Fact]
    public async Task A_sale_that_may_not_be_posted_is_reported_apart_from_a_sap_refusal()
    {
        var handler = Handler(reference => reference switch
        {
            "B" => Errors.DesktopSales.SaleNotPostable("B", "This sale is already in SAP."),
            "C" => Errors.DesktopSales.SaleNotFound("C"),
            _ => Posted(reference)
        });

        var result = await handler.Handle(Command("A", "B", "C"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.NotPostable);
        Assert.Equal(0, result.Value.Failed);
    }

    [Fact]
    public async Task An_account_that_may_not_post_is_refused_the_whole_request()
    {
        // A statement about the caller, not about the sale, so it will be equally true of every
        // other reference. Reporting it as one row among many would present a permissions failure
        // as a data problem, and the operator would go looking at the sale.
        var handler = Handler(reference => reference switch
        {
            "B" => Errors.DesktopSales.SalesReadOutsideScope("KEFGRS", "KEFSHOP"),
            _ => Posted(reference)
        });

        var result = await handler.Handle(Command("A", "B", "C"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
    }

    [Fact]
    public async Task The_same_sale_named_twice_is_posted_once()
    {
        var sent = new List<string>();
        var handler = Handler(reference =>
        {
            sent.Add(reference);
            return Posted(reference);
        });

        var result = await handler.Handle(Command("A", "A", " A ", "B"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new[] { "A", "B" }, sent);

        // The claim would have answered the second "A" with a replay rather than a second invoice,
        // so this is about the answer rather than about safety: one sale reported on two rows is a
        // worse answer than one sale reported once.
        Assert.Equal(2, result.Value.Requested);
    }

    [Fact]
    public async Task A_request_naming_nothing_is_refused()
    {
        var handler = Handler(Posted);

        var result = await handler.Handle(Command("   ", ""), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.BulkPostReferencesRequired", result.FirstError.Code);
    }

    private static PostDesktopSalesToSapCommand Command(params string[] references)
        => new(Caller, references);

    private static ErrorOr<DesktopSalePostResult> Posted(string reference)
        => new DesktopSalePostResult(
            reference, DesktopSalePostOutcomes.Posted, 900, 900, $"Posted to SAP as invoice 900.");

    /// <remarks>
    /// The single-sale command is stubbed rather than run, deliberately. What is being pinned here is
    /// the loop: which outcomes it collects, which it stops on, and what it sends. The posting itself
    /// is pinned where it lives, in <see cref="DesktopSalePostGuardTests"/>.
    /// </remarks>
    private static PostDesktopSalesToSapHandler Handler(
        Func<string, ErrorOr<DesktopSalePostResult>> answer)
        => new(
            StubProxy.For<IMediator>((method, args) =>
            {
                if (method.Name != nameof(IMediator.Send) || args?[0] is not PostDesktopSaleToSapCommand command)
                {
                    throw new InvalidOperationException($"Unexpected mediator call: {method.Name}");
                }

                return Task.FromResult(answer(command.ExternalReferenceId));
            }),
            NullLogger<PostDesktopSalesToSapHandler>.Instance);
}
