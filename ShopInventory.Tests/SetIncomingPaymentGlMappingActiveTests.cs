using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.DailyIncomingPayments.Commands.SetIncomingPaymentGlMappingActive;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The per-partner switch for the daily incoming payment: it flips one partner, leaves its accounts alone,
/// and refuses a partner that has no accounts to post to.
/// </summary>
public sealed class SetIncomingPaymentGlMappingActiveTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SetIncomingPaymentGlMappingActiveTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var context = NewContext();
        context.Database.EnsureCreated();
        context.IncomingPaymentGlMappings.Add(new IncomingPaymentGlMappingEntity
        {
            CardCode = "BP-1",
            CashAccount = "700300",
            ElectronicAccount = "701100",
            NeedsReview = true,
            IsActive = false
        });
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Switching_on_flips_only_the_switch()
    {
        var result = await Send("BP-1", true);

        Assert.False(result.IsError);
        Assert.True(result.Value.IsActive);

        await using var context = NewContext();
        var saved = await context.IncomingPaymentGlMappings.AsNoTracking().SingleAsync(m => m.CardCode == "BP-1");
        Assert.True(saved.IsActive);
        Assert.Equal("700300", saved.CashAccount);
        Assert.Equal("701100", saved.ElectronicAccount);
        // Only saving the full form confirms the accounts, so the review flag stays.
        Assert.True(saved.NeedsReview);
        Assert.Equal("tester", saved.UpdatedBy);
    }

    [Fact]
    public async Task Switching_off_again_turns_it_off()
    {
        await Send("BP-1", true);
        var result = await Send("BP-1", false);

        Assert.False(result.Value.IsActive);
        await using var context = NewContext();
        Assert.False((await context.IncomingPaymentGlMappings.AsNoTracking().SingleAsync(m => m.CardCode == "BP-1")).IsActive);
    }

    [Fact]
    public async Task A_partner_with_no_accounts_cannot_be_switched_on()
    {
        var result = await Send("BP-9", true);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
        await using var context = NewContext();
        Assert.False(await context.IncomingPaymentGlMappings.AnyAsync(m => m.CardCode == "BP-9"));
    }

    private async Task<ErrorOr<Features.DailyIncomingPayments.IncomingPaymentGlMappingDto>> Send(string cardCode, bool isActive)
    {
        await using var context = NewContext();
        var handler = new SetIncomingPaymentGlMappingActiveHandler(
            context,
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            NullLogger<SetIncomingPaymentGlMappingActiveHandler>.Instance);

        return await handler.Handle(new SetIncomingPaymentGlMappingActiveCommand(cardCode, isActive, "tester"), CancellationToken.None);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
}
