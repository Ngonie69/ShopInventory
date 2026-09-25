using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Idempotency;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Features.DesktopIntegration.Commands.UpdatePostingDatePolicy;
using ShopInventory.Features.DesktopIntegration.Queries.GetPostingDatePolicy;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A till may choose the day its sale is posted to SAP under, but only while an admin has switched it on
/// from Web → Settings. Off, every sale posts on the day it was sold.
/// </summary>
public sealed class PostingDatePolicyTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 9, 25);

    private readonly SqliteConnection _connection;

    public PostingDatePolicyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------
    // The rule
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-09-25")]
    public void No_date_or_today_posts_on_the_day_of_sale_whatever_the_switch_says(string? requested)
    {
        Assert.Null(CreateDesktopSaleHandler.ResolvePostingDate(requested, Today, customDatesAllowed: false).Value);
        Assert.Null(CreateDesktopSaleHandler.ResolvePostingDate(requested, Today, customDatesAllowed: true).Value);
    }

    [Fact]
    public void An_earlier_day_is_used_when_the_switch_is_on()
    {
        var result = CreateDesktopSaleHandler.ResolvePostingDate("2026-09-20", Today, customDatesAllowed: true);

        Assert.Equal(new DateTime(2026, 9, 20), result.Value);
    }

    [Fact]
    public void An_earlier_day_is_refused_not_replaced_when_the_switch_is_off()
    {
        var result = CreateDesktopSaleHandler.ResolvePostingDate("2026-09-20", Today, customDatesAllowed: false);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.PostingDateNotAllowed", result.FirstError.Code);
        // A 400, so the till shows the cashier the reason rather than "it may or may not have been created".
        Assert.Equal(ErrorType.Validation, result.FirstError.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_later_day_is_refused_even_with_the_switch_on(bool allowed)
    {
        var result = CreateDesktopSaleHandler.ResolvePostingDate("2026-09-26", Today, allowed);

        Assert.Equal("DesktopSales.PostingDateInFuture", result.FirstError.Code);
    }

    [Theory]
    [InlineData("2026-09-20", true)]
    [InlineData("2026-09-25", false)]
    [InlineData(null, false)]
    [InlineData("rubbish", false)]
    public void The_switch_is_read_only_when_another_day_was_asked_for(string? requested, bool expected) =>
        Assert.Equal(expected, CreateDesktopSaleHandler.RequestsCustomPostingDate(requested, Today));

    [Theory]
    [InlineData("20/09/2026")]
    [InlineData("2026-9-20")]
    [InlineData("yesterday")]
    public void A_posting_date_not_in_iso_form_fails_validation(string requested)
    {
        var result = new CreateDesktopSaleValidator().Validate(new CreateDesktopSaleCommand(
            new CreateDesktopSaleRequest { PostingDate = requested, Lines = [new() { ItemCode = "ICA004", Quantity = 1 }] },
            Guid.NewGuid()));

        Assert.Contains(result.Errors, error => error.PropertyName == "Request.PostingDate");
    }

    /// <summary>
    /// The replay guard compares a request's hash with the one stored on the sale. A sale made before the
    /// field shipped must still match its own retry afterwards, so an absent date must not change the JSON.
    /// </summary>
    [Fact]
    public void An_absent_posting_date_leaves_the_idempotency_hash_as_it_was()
    {
        var request = new CreateDesktopSaleRequest { CardCode = "CIS006", Lines = [new() { ItemCode = "ICA004", Quantity = 1 }] };

        var json = JsonSerializer.Serialize(request, IdempotencyRequestHash.SerializerOptions);
        Assert.DoesNotContain("postingDate", json, StringComparison.OrdinalIgnoreCase);

        request.PostingDate = "2026-09-20";
        Assert.Contains("\"postingDate\":\"2026-09-20\"", JsonSerializer.Serialize(request, IdempotencyRequestHash.SerializerOptions));
    }

    // ---------------------------------------------------------------
    // The switch
    // ---------------------------------------------------------------

    [Fact]
    public async Task Nobody_has_saved_it_so_it_is_off()
    {
        await using var context = NewContext();

        var policy = await new GetPostingDatePolicyHandler(context).Handle(new GetPostingDatePolicyQuery(), CancellationToken.None);

        Assert.False(policy.Value.AllowCustomPostingDate);
        Assert.Null(policy.Value.UpdatedAtUtc);
        Assert.Equal(AuditService.ToCAT(DateTime.UtcNow).Date, policy.Value.TodayCat);
        Assert.False(await PostingDatePolicyKeys.IsAllowedAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task An_admin_switches_it_on_and_off_and_is_named()
    {
        var on = await UpdateAsync(allow: true, who: "ngoni");

        Assert.True(on.AllowCustomPostingDate);
        Assert.Equal("ngoni", on.UpdatedBy);
        Assert.NotNull(on.UpdatedAtUtc);
        await using (var context = NewContext())
        {
            Assert.True(await PostingDatePolicyKeys.IsAllowedAsync(context, CancellationToken.None));
        }

        var off = await UpdateAsync(allow: false, who: "someone-else");

        Assert.False(off.AllowCustomPostingDate);
        Assert.Equal("someone-else", off.UpdatedBy);
        await using (var context = NewContext())
        {
            Assert.False(await PostingDatePolicyKeys.IsAllowedAsync(context, CancellationToken.None));
            // Saved in place, not appended.
            Assert.Single(context.SystemConfigs, config => config.Key == PostingDatePolicyKeys.AllowCustomPostingDate);
        }
    }

    private async Task<PostingDatePolicy> UpdateAsync(bool allow, string who)
    {
        await using var context = NewContext();
        var mediator = StubProxy.For<IMediator>((method, args) =>
            args?[0] is GetPostingDatePolicyQuery query
                ? new GetPostingDatePolicyHandler(context).Handle(query, CancellationToken.None)
                : throw new InvalidOperationException($"Unexpected {method.Name}"));

        var result = await new UpdatePostingDatePolicyHandler(
                context,
                mediator,
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                NullLogger<UpdatePostingDatePolicyHandler>.Instance)
            .Handle(new UpdatePostingDatePolicyCommand(Guid.NewGuid(), who, allow), CancellationToken.None);

        return result.Value;
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
}
