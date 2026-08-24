using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetInvoicesByCustomer;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesChannelCustomers;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerInvoices;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Who may look outside their own route at a whole trade channel, and what they get when they do.
/// </summary>
/// <remarks>
/// Every other customer read a handset makes is scoped to the signed-in rep. These two are not:
/// 'General Trade' is 157 customers company-wide in production, most of them on somebody else's
/// route. So the gate is the feature, and a gate that silently stops holding is a rep reading another
/// rep's trading history with nothing on the screen to say so.
/// </remarks>
public sealed class ChannelCustomerAccessTests : IDisposable
{
    private static readonly Guid Actor = Guid.Parse("2b7c1a94-3f5e-4d16-8a02-9c4e5f6a7b81");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ChannelCustomerAccessTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- the rule itself -------------------------------------------------------------------

    [Theory]
    [InlineData(ApplicationRoles.Admin)]
    [InlineData(ApplicationRoles.StockController)]
    public void The_two_named_roles_may_see_a_whole_channel(string role)
    {
        Assert.True(ChannelCustomerAccess.MaySeeWholeChannel(role));
    }

    /// <summary>
    /// The roles that actually drive a route are the ones this keeps out, which is the whole point:
    /// a rep's own customers already reach them through the route-scoped reads.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.Sales)]
    [InlineData(ApplicationRoles.Adr)]
    [InlineData(ApplicationRoles.SalesRep)]
    [InlineData(ApplicationRoles.Manager)]
    [InlineData(ApplicationRoles.Driver)]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Something the server started issuing")]
    public void Everyone_else_may_not(string? role)
    {
        Assert.False(ChannelCustomerAccess.MaySeeWholeChannel(role));
    }

    /// <summary>
    /// The role is the server's column to spell. The handset's own UserRole.Parse folds case and trims
    /// for exactly this reason, after "ADR " once walked an ordering rep through a rep-only gate.
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData(" Admin ")]
    [InlineData("STOCKCONTROLLER")]
    [InlineData("stockController ")]
    public void The_role_is_read_the_way_the_server_might_spell_it(string role)
    {
        Assert.True(ChannelCustomerAccess.MaySeeWholeChannel(role));
    }

    /// <summary>
    /// Verified against OCRD in both company databases rather than assumed, and matched exactly at
    /// the server — 'General Trade' and 'Trade' are different books of customers.
    /// </summary>
    [Fact]
    public void The_channel_is_spelled_the_way_sap_holds_it()
    {
        Assert.Equal("General Trade", ChannelCustomerAccess.GeneralTrade);
    }

    // ---- listing a channel's customers -----------------------------------------------------

    [Fact]
    public async Task A_stock_controller_is_given_the_channels_customers()
    {
        SeedUser(ApplicationRoles.StockController);

        var result = await CustomersHandler().Handle(
            new GetVanSalesChannelCustomersQuery(Actor, ChannelCustomerAccess.GeneralTrade),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.Count);

        // Ordered by name, so a person can find a shop in a list of 157.
        Assert.Equal("CHITUNGWIZA BOTTLE STORE", result.Value[0].Name);
        Assert.Equal("C0002", result.Value[0].Code);
        Assert.Equal("General Trade", result.Value[0].Channel);
        Assert.False(result.Value[0].Active);
    }

    [Fact]
    public async Task A_sales_rep_is_refused_the_channel_listing()
    {
        SeedUser(ApplicationRoles.Sales);

        var result = await CustomersHandler().Handle(
            new GetVanSalesChannelCustomersQuery(Actor, ChannelCustomerAccess.GeneralTrade),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);
    }

    /// <summary>
    /// An account switched off at the office keeps no view it had, and that check comes before the
    /// SAP read rather than after it.
    /// </summary>
    [Fact]
    public async Task A_deactivated_administrator_is_refused()
    {
        SeedUser(ApplicationRoles.Admin, active: false);

        var result = await CustomersHandler(sapMustNotBeCalled: true).Handle(
            new GetVanSalesChannelCustomersQuery(Actor, ChannelCustomerAccess.GeneralTrade),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    // ---- one customer's invoices -----------------------------------------------------------

    /// <summary>
    /// The delegated read must not carry the mobile assigned-customer restriction. That rule would
    /// refuse nearly every customer in a channel, because a channel is mostly other people's route —
    /// so the gate that belongs here is the role one, which the handler applies itself.
    /// </summary>
    [Fact]
    public async Task The_invoice_read_is_not_narrowed_to_the_reps_own_customers()
    {
        SeedUser(ApplicationRoles.Admin);
        var sent = new List<GetInvoicesByCustomerQuery>();

        var result = await InvoicesHandler(sent).Handle(
            new GetVanSalesCustomerInvoicesQuery(Actor, " C0002 ", null, null, null, null),
            CancellationToken.None);

        Assert.False(result.IsError);
        var delegated = Assert.Single(sent);
        Assert.False(delegated.RestrictToAssignedCustomers);
        Assert.Equal("C0002", delegated.CardCode);
        Assert.Equal(Actor, delegated.RequestingUserId);
    }

    [Fact]
    public async Task A_sales_rep_is_refused_another_shops_invoices()
    {
        SeedUser(ApplicationRoles.Sales);
        var sent = new List<GetInvoicesByCustomerQuery>();

        var result = await InvoicesHandler(sent).Handle(
            new GetVanSalesCustomerInvoicesQuery(Actor, "C0002", null, null, null, null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Forbidden, result.FirstError.Type);

        // Refused before the read, not after it.
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_request_with_no_customer_code_is_refused_rather_than_read()
    {
        SeedUser(ApplicationRoles.Admin);
        var sent = new List<GetInvoicesByCustomerQuery>();

        var result = await InvoicesHandler(sent).Handle(
            new GetVanSalesCustomerInvoicesQuery(Actor, "   ", null, null, null, null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(sent);
    }

    // ---- fixtures --------------------------------------------------------------------------

    private void SeedUser(string role, bool active = true)
    {
        _context.Users.Add(new User
        {
            Id = Actor,
            Username = "channel-viewer",
            PasswordHash = "not-a-real-hash",
            Role = role,
            IsActive = active
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private GetVanSalesChannelCustomersHandler CustomersHandler(bool sapMustNotBeCalled = false) =>
        new(
            _context,
            sapMustNotBeCalled
                ? StubProxy.Unused<ISAPServiceLayerClient>()
                : StubProxy.For<ISAPServiceLayerClient>((method, args) =>
                {
                    if (method.Name != nameof(ISAPServiceLayerClient.GetCustomersByChannelAsync))
                        throw new InvalidOperationException($"Unexpected call to {method.Name}");

                    Assert.Equal(ChannelCustomerAccess.GeneralTrade, (string)args![0]!);

                    // Deliberately out of order and with a frozen account, so the ordering and the
                    // active flag are actually exercised.
                    return Task.FromResult(new List<BusinessPartnerDto>
                    {
                        new()
                        {
                            CardCode = "C0001", CardName = "MAI RUFARO TUCKSHOP", CardType = "cCustomer",
                            Channel = "General Trade", Phone1 = "0772000000", City = "Chitungwiza",
                            Currency = "USD", Balance = 240.50m, IsActive = true
                        },
                        new()
                        {
                            CardCode = "C0002", CardName = "CHITUNGWIZA BOTTLE STORE", CardType = "cCustomer",
                            Channel = "General Trade", Currency = "USD", IsActive = false
                        }
                    });
                }),
            NullLogger<GetVanSalesChannelCustomersHandler>.Instance);

    private GetVanSalesCustomerInvoicesHandler InvoicesHandler(List<GetInvoicesByCustomerQuery> sent) =>
        new(
            _context,
            StubProxy.For<IMediator>((method, args) =>
            {
                if (method.Name != nameof(IMediator.Send))
                    throw new InvalidOperationException($"Unexpected call to {method.Name}");

                sent.Add((GetInvoicesByCustomerQuery)args![0]!);

                return Task.FromResult<ErrorOr<InvoiceDateResponseDto>>(new InvoiceDateResponseDto
                {
                    Customer = "C0002",
                    Invoices = []
                });
            }),
            NullLogger<GetVanSalesCustomerInvoicesHandler>.Instance);
}
