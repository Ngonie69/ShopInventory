using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.RequestVanSaleWhatsApp;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A van rep sending a sale's invoice to the number the customer gave: whose sales they may send, and
/// that asking twice never queues two sends.
/// </summary>
public sealed class VanSaleWhatsAppTests : IDisposable
{
    private const string VanCard = "VAN008";
    private const string VanOrder = "VO-20261009-0042";
    private static readonly Guid SecondRep = Guid.Parse("22222222-3333-4444-5555-666666666666");
    private static readonly Guid OtherVanRep = Guid.Parse("33333333-4444-5555-6666-777777777777");

    private readonly CustomerDocumentTestKit _kit = new();
    private readonly RecordingAuditService _audit = new();
    private readonly RecordingDispatchTrigger _trigger = new();

    public VanSaleWhatsAppTests()
    {
        using var context = _kit.NewContext();
        context.Users.AddRange(
            new User { Id = SecondRep, Username = "rep2", PasswordHash = "x", Role = "Sales", FirstName = "Farai", LastName = "Dube", IsActive = true, AssignedBusinessPartnerCode = VanCard },
            new User { Id = OtherVanRep, Username = "rep9", PasswordHash = "x", Role = "Sales", FirstName = "Tino", LastName = "Ncube", IsActive = true, AssignedBusinessPartnerCode = "VAN009" });
        context.RouteCustomers.Add(new RouteCustomerEntity
        {
            Id = 41, AssignedBusinessPartnerCode = VanCard, Code = "S41", Name = "Mbare Tuck Shop",
            Address = "Stand 12, Mbare", VatNumber = "220000041", Phone = "0772000041"
        });
        context.SaveChanges();
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task The_reps_own_sale_is_queued_against_the_sale_until_SAP_has_it()
    {
        await _kit.SetRuntimeAsync();
        var saleId = await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.False(result.Value.AlreadyRequested);
        Assert.Equal("Pending", result.Value.Status);
        Assert.Equal(1, _trigger.Triggered);

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(CustomerDocumentDeliveryTrigger.Counter, row.Trigger);
        Assert.Equal(saleId, row.DesktopSaleId);
        Assert.Null(row.SapDocEntry);
        Assert.Equal(DesktopSaleNumber.Format(saleId), row.DocumentNumber);
        Assert.Equal(VanOrder, row.SaleReference);
        Assert.Equal(VanCard, row.CardCode);
        Assert.Equal(41, row.RouteCustomerId);
        Assert.Equal("Mbare Tuck Shop", row.CardName);
        Assert.Equal("+263771234567", row.RecipientE164);
        Assert.True(row.ConsentAffirmed);
        Assert.Contains(AuditActions.RequestDocumentWhatsApp, _audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task A_sale_already_in_SAP_is_queued_as_its_invoice()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync(sapDocEntry: 2400777, sapDocNum: 780777);

        await RequestAsync(CustomerDocumentTestKit.Cashier);

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(2400777, row.SapDocEntry);
        Assert.Equal(780777, row.SapDocNum);
        Assert.Equal("780777", row.DocumentNumber);
    }

    [Fact]
    public async Task Asking_again_for_the_same_number_hands_back_the_first_send()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();

        var first = await RequestAsync(CustomerDocumentTestKit.Cashier);
        var again = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: "+263 77 123 4567");

        Assert.True(again.Value.AlreadyRequested);
        Assert.Equal(first.Value.DeliveryId, again.Value.DeliveryId);
        Assert.Single(await DeliveriesAsync());
        Assert.Equal(1, _trigger.Triggered);
    }

    [Fact]
    public async Task The_second_rep_on_the_same_van_may_send_it()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();

        var result = await RequestAsync(SecondRep);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
    }

    [Fact]
    public async Task Another_vans_sale_answers_as_if_it_did_not_exist()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();

        var result = await RequestAsync(OtherVanRep);

        Assert.Equal("CustomerDocuments.VanSaleNotFound", result.FirstError.Code);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task A_till_sale_under_the_same_reference_is_not_a_van_sale()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync(source: "KefalosTill");

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.Equal("CustomerDocuments.VanSaleNotFound", result.FirstError.Code);
    }

    [Theory]
    [InlineData("0771234567", false, "CustomerDocuments.ConsentRequired")]
    [InlineData("hello", true, "CustomerDocuments.InvalidPhone")]
    public async Task A_number_without_consent_or_that_is_not_a_number_is_refused(string phone, bool consent, string expected)
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, phone, consent);

        Assert.Equal(expected, result.FirstError.Code);
        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task A_number_that_opted_out_is_refused()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await _kit.AddContactAsync(phone: "+263771234567", optedOutAtUtc: DateTime.UtcNow.AddDays(-2));

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.Equal("CustomerDocuments.NumberOptedOut", result.FirstError.Code);
    }

    [Fact]
    public async Task A_rep_who_has_sent_their_days_allowance_is_refused()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await _kit.AddDeliveryAsync(row =>
        {
            row.Trigger = CustomerDocumentDeliveryTrigger.Counter;
            row.RecipientE164 = "+263770000001";
            row.CreatedAtUtc = DateTime.UtcNow;
        });

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, settings: s => s.MaxVanSaleSendsPerUserPerDay = 1);

        Assert.Equal("CustomerDocuments.VanSaleSendLimitReached", result.FirstError.Code);
    }

    [Fact]
    public async Task Nothing_is_queued_while_no_session_is_chosen()
    {
        await _kit.SetRuntimeAsync(sessionId: null);
        await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.Equal("CustomerDocuments.SessionNotConfigured", result.FirstError.Code);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<int> AddSaleAsync(int? sapDocEntry = null, int? sapDocNum = null, string source = SaleSourceSystems.VanSalesOnline)
    {
        await using var context = _kit.NewContext();
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = VanOrder,
            SourceSystem = source,
            CardCode = VanCard,
            CardName = "Van Sales West 2",
            RouteCustomerId = 41,
            RouteCustomerCode = "S41",
            RouteCustomerName = "Mbare Tuck Shop",
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 48.30m,
            Currency = "USD",
            WarehouseCode = "VAN004",
            CreatedBy = CustomerDocumentTestKit.Cashier.ToString(),
            SapDocEntry = sapDocEntry,
            SapDocNum = sapDocNum
        };
        context.DesktopSales.Add(sale);
        await context.SaveChangesAsync();
        return sale.Id;
    }

    private async Task<ErrorOr<VanSaleWhatsAppResponse>> RequestAsync(
        Guid userId,
        string phone = "0771234567",
        bool consent = true,
        Action<CustomerDocumentDeliverySettings>? settings = null)
    {
        await using var context = _kit.NewContext();
        var handler = new RequestVanSaleWhatsAppHandler(
            context,
            Options.Create(CustomerDocumentTestKit.Settings(settings)),
            _trigger,
            _audit,
            NullLogger<RequestVanSaleWhatsAppHandler>.Instance);

        return await handler.Handle(
            new RequestVanSaleWhatsAppCommand(userId, VanOrder, new VanSaleWhatsAppRequest { Phone = phone, Consent = consent }),
            CancellationToken.None);
    }

    private async Task<List<CustomerDocumentDeliveryEntity>> DeliveriesAsync()
    {
        await using var context = _kit.NewContext();
        return await context.CustomerDocumentDeliveries.AsNoTracking().ToListAsync();
    }
}
