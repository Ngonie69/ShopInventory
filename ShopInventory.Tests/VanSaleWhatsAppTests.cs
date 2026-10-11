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
    private readonly FakeOpenWAClient _gateway = new();

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
            row.ConsentAffirmed = true;
            row.CreatedAtUtc = DateTime.UtcNow;
        });

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, settings: s => s.MaxVanSaleSendsPerUserPerDay = 1);

        Assert.Equal("CustomerDocuments.VanSaleSendLimitReached", result.FirstError.Code);
    }

    [Fact]
    public async Task With_no_session_chosen_the_send_is_still_queued()
    {
        await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Single(await DeliveriesAsync());
        Assert.Equal(CustomerDocumentTestKit.SessionId, await _kit.SavedSessionIdAsync());
    }

    [Fact]
    public async Task A_gateway_with_no_number_is_the_offices_to_fix_so_the_rep_is_not_refused()
    {
        await AddSaleAsync();
        _gateway.SessionStatus = "disconnected";

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Single(await DeliveriesAsync());
    }

    [Fact]
    public async Task Nothing_is_queued_while_an_administrator_has_stopped_sending()
    {
        await _kit.SetRuntimeAsync(sessionId: null, stopped: true);
        await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.Equal("CustomerDocuments.SendingStopped", result.FirstError.Code);
        Assert.Empty(await DeliveriesAsync());
    }

    // ── The number is kept, and asked for once ──────────────────────────

    [Fact]
    public async Task A_number_given_at_the_sale_is_saved_on_the_shop_for_automatic_invoices()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.True(result.Value.Saved);
        Assert.Contains("next invoices", result.Value.Message);

        var contact = Assert.Single(await ContactsAsync());
        Assert.Equal(41, contact.RouteCustomerId);
        Assert.Null(contact.CardCode);
        Assert.Equal("+263771234567", contact.PhoneE164);
        Assert.True(contact.AutoSendInvoices);
        Assert.Equal(WhatsAppConsentSource.VanHandset, contact.ConsentSource);
        Assert.Equal(CustomerDocumentTestKit.Cashier, contact.ConsentRecordedByUserId);
        Assert.Contains(VanOrder, contact.ConsentNote);

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(contact.Id, row.ContactId);
        Assert.True(row.ConsentAffirmed);
    }

    [Fact]
    public async Task A_shop_with_a_saved_number_is_sent_its_invoice_without_being_asked()
    {
        await _kit.SetRuntimeAsync();
        var saleId = await AddSaleAsync();
        var contact = await _kit.AddShopContactAsync(41, "+263772000041");

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.False(result.Value.NeedsNumber);
        Assert.True(result.Value.Saved);
        Assert.False(result.Value.AlreadyRequested);
        Assert.Equal(["*** *** 0041"], result.Value.Recipients);
        Assert.Equal(1, _trigger.Triggered);

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(CustomerDocumentDeliveryTrigger.Counter, row.Trigger);
        Assert.Equal(saleId, row.DesktopSaleId);
        Assert.Equal(contact.Id, row.ContactId);
        Assert.Equal("+263772000041", row.RecipientE164);
        // Sent on the consent the number was saved under, not on anything said at this sale.
        Assert.False(row.ConsentAffirmed);
    }

    [Fact]
    public async Task A_shop_with_no_saved_number_answers_that_one_is_needed_and_queues_nothing()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.True(result.Value.NeedsNumber);
        Assert.Empty(result.Value.Recipients);
        Assert.Empty(await DeliveriesAsync());
        Assert.Equal(0, _trigger.Triggered);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_number_set_to_only_when_asked_or_opted_out_is_not_sent_to_unasked(bool autoSend, bool optedOut)
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await _kit.AddShopContactAsync(41, "+263772000041", autoSend, optedOut ? DateTime.UtcNow.AddDays(-1) : null);

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false);

        Assert.True(result.Value.NeedsNumber);
        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task The_sale_after_a_number_was_given_needs_no_number()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await RequestAsync(CustomerDocumentTestKit.Cashier);
        await AddSaleAsync(vanOrder: "VO-20261009-0043");

        var next = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false, vanOrder: "VO-20261009-0043");

        Assert.False(next.Value.NeedsNumber);
        Assert.Equal(["*** *** 4567"], next.Value.Recipients);
        Assert.Equal(2, (await DeliveriesAsync()).Count);
        Assert.Single(await ContactsAsync());
    }

    [Fact]
    public async Task Asking_again_with_no_number_queues_nothing_more()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await _kit.AddShopContactAsync(41, "+263772000041");

        await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false);
        var again = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false);

        Assert.True(again.Value.AlreadyRequested);
        Assert.Single(await DeliveriesAsync());
        Assert.Equal(1, _trigger.Triggered);
    }

    [Fact]
    public async Task A_number_the_office_or_the_scan_already_sent_this_invoice_to_is_not_sent_a_second_copy()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync(sapDocEntry: 2400777, sapDocNum: 780777);
        var contact = await _kit.AddShopContactAsync(41, "+263772000041");
        await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400777;
            row.SapDocNum = 780777;
            row.Trigger = CustomerDocumentDeliveryTrigger.Auto;
            row.ContactId = contact.Id;
            row.RecipientE164 = "+263772000041";
        });

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false);

        Assert.True(result.Value.AlreadyRequested);
        Assert.Single(await DeliveriesAsync());
    }

    [Fact]
    public async Task A_number_the_office_set_to_only_when_asked_stays_that_way_when_a_rep_sends_to_it()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        var contact = await _kit.AddShopContactAsync(41, "+263771234567", autoSend: false);

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier);

        Assert.True(result.Value.Saved);
        var saved = Assert.Single(await ContactsAsync());
        Assert.False(saved.AutoSendInvoices);
        Assert.Equal(WhatsAppConsentSource.Web, saved.ConsentSource);
        Assert.Equal(contact.Id, Assert.Single(await DeliveriesAsync()).ContactId);
    }

    [Fact]
    public async Task A_shop_with_all_the_numbers_it_may_have_is_still_sent_to_the_one_given()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await _kit.AddShopContactAsync(41, "+263772000041");

        var result = await RequestAsync(CustomerDocumentTestKit.Cashier, settings: s => s.MaxContactsPerOwner = 1);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.False(result.Value.Saved);
        Assert.Single(await ContactsAsync());
        var row = Assert.Single(await DeliveriesAsync());
        Assert.Null(row.ContactId);
        Assert.Equal("+263771234567", row.RecipientE164);
    }

    [Fact]
    public async Task Sends_to_a_saved_number_do_not_use_up_the_reps_allowance_for_typed_ones()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync();
        await _kit.AddShopContactAsync(41, "+263772000041");
        await RequestAsync(CustomerDocumentTestKit.Cashier, phone: null, consent: false, settings: s => s.MaxVanSaleSendsPerUserPerDay = 1);

        var typed = await RequestAsync(CustomerDocumentTestKit.Cashier, settings: s => s.MaxVanSaleSendsPerUserPerDay = 1);

        Assert.False(typed.IsError, typed.IsError ? typed.FirstError.Description : null);
        Assert.Equal(2, (await DeliveriesAsync()).Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<int> AddSaleAsync(
        int? sapDocEntry = null,
        int? sapDocNum = null,
        string source = SaleSourceSystems.VanSalesOnline,
        string vanOrder = VanOrder)
    {
        await using var context = _kit.NewContext();
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = vanOrder,
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
        string? phone = "0771234567",
        bool consent = true,
        Action<CustomerDocumentDeliverySettings>? settings = null,
        string vanOrder = VanOrder)
    {
        await using var context = _kit.NewContext();
        var handler = new RequestVanSaleWhatsAppHandler(
            context,
            Options.Create(CustomerDocumentTestKit.Settings(settings)),
            _gateway,
            Options.Create(CustomerDocumentTestKit.Gateway()),
            _trigger,
            _audit,
            NullLogger<RequestVanSaleWhatsAppHandler>.Instance);

        return await handler.Handle(
            new RequestVanSaleWhatsAppCommand(userId, vanOrder, new VanSaleWhatsAppRequest { Phone = phone, Consent = consent }),
            CancellationToken.None);
    }

    private async Task<List<CustomerWhatsAppContactEntity>> ContactsAsync()
    {
        await using var context = _kit.NewContext();
        return await context.CustomerWhatsAppContacts.AsNoTracking().ToListAsync();
    }

    private async Task<List<CustomerDocumentDeliveryEntity>> DeliveriesAsync()
    {
        await using var context = _kit.NewContext();
        return await context.CustomerDocumentDeliveries.AsNoTracking().ToListAsync();
    }
}
