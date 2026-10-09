using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;
using ShopInventory.Features.CustomerDocuments.Commands.ScanNewInvoicesForDelivery;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A van invoice belongs to the shop it was sold to, not to the van's card it was billed to: which
/// shop that is, that its own numbers receive it, and that it never reaches them twice.
/// </summary>
public sealed class RouteCustomerInvoiceTests : IDisposable
{
    private const string VanCard = "VAN008";
    private const int Shop = 41;
    private const int OtherShop = 42;
    private static readonly DateTime Now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    private readonly CustomerDocumentTestKit _kit = new();
    private readonly RecordingDispatchTrigger _trigger = new();
    private readonly RecordingAuditService _audit = new();

    public RouteCustomerInvoiceTests()
    {
        using var context = _kit.NewContext();
        context.RouteCustomers.AddRange(
            new RouteCustomerEntity { Id = Shop, AssignedBusinessPartnerCode = VanCard, Code = "S41", Name = "Mbare Tuck Shop" },
            new RouteCustomerEntity { Id = OtherShop, AssignedBusinessPartnerCode = VanCard, Code = "S42", Name = "Sakubva Store" });
        context.SaveChanges();
    }

    public void Dispose() => _kit.Dispose();

    // ── Which shop ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_offline_van_sale_names_its_shop()
    {
        var saleId = await AddSaleAsync("VO-1", Shop);

        var link = await ResolveAsync(Invoice(5001, "VO-1"));

        Assert.Equal(Shop, link!.RouteCustomerId);
        Assert.Equal(saleId, link.DesktopSaleId);
    }

    [Fact]
    public async Task An_online_sale_with_only_a_confirmed_reservation_names_its_shop()
    {
        await AddReservationAsync("VO-2", Shop, ReservationStatus.Confirmed);

        var link = await ResolveAsync(Invoice(5002, "VO-2"));

        Assert.Equal(Shop, link!.RouteCustomerId);
        Assert.Null(link.DesktopSaleId);
    }

    [Theory]
    [InlineData(ReservationStatus.Pending)]
    [InlineData(ReservationStatus.Expired)]
    public async Task A_reservation_that_never_became_a_sale_names_nobody(string status)
    {
        await AddReservationAsync("VO-3", Shop, status);

        Assert.Null(await ResolveAsync(Invoice(5003, "VO-3")));
    }

    [Fact]
    public async Task A_sale_row_that_records_another_invoice_is_a_stale_link_and_no_reservation_stands_in()
    {
        await AddSaleAsync("VO-4", Shop, sapDocEntry: 4999);
        await AddReservationAsync("VO-4", OtherShop, ReservationStatus.Confirmed);

        Assert.Null(await ResolveAsync(Invoice(5004, "VO-4")));
    }

    [Fact]
    public async Task A_sale_billed_to_another_card_or_made_at_a_till_names_nobody()
    {
        await AddSaleAsync("VO-5", Shop, cardCode: "VAN009");
        await AddSaleAsync("VO-6", Shop, source: "KefalosTill");

        Assert.Null(await ResolveAsync(Invoice(5005, "VO-5")));
        Assert.Null(await ResolveAsync(Invoice(5006, "VO-6")));
    }

    // ── Automatic sends ─────────────────────────────────────────────────

    [Fact]
    public async Task The_scan_sends_a_van_invoice_to_its_shops_automatic_numbers_naming_the_shop()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await AddSaleAsync("VO-7", Shop);
        var wanted = await AddShopContactAsync(Shop, "+263771111111");
        await AddShopContactAsync(OtherShop, "+263772222222");

        await ScanAsync(Invoice(5007, "VO-7"));

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(CustomerDocumentDeliveryTrigger.Auto, row.Trigger);
        Assert.Equal(wanted.Id, row.ContactId);
        Assert.Equal(Shop, row.RouteCustomerId);
        Assert.Equal("Mbare Tuck Shop", row.CardName);
        Assert.Equal(VanCard, row.CardCode);
        Assert.Equal(1, _trigger.Triggered);
    }

    [Fact]
    public async Task A_number_the_rep_sent_it_to_at_the_van_is_not_sent_it_again()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        var saleId = await AddSaleAsync("VO-8", Shop);
        await AddShopContactAsync(Shop, "+263771111111");
        await _kit.AddDeliveryAsync(row =>
        {
            // Asked at the van before the office posted it: it names the sale, not yet the invoice.
            row.SapDocEntry = null;
            row.SapDocNum = null;
            row.DesktopSaleId = saleId;
            row.Trigger = CustomerDocumentDeliveryTrigger.Counter;
            row.RecipientE164 = "+263771111111";
        });

        await ScanAsync(Invoice(5008, "VO-8"));

        Assert.DoesNotContain(await DeliveriesAsync(), row => row.Trigger == CustomerDocumentDeliveryTrigger.Auto);
    }

    [Fact]
    public async Task A_number_someone_already_sent_the_invoice_to_by_hand_is_not_sent_it_again()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await AddSaleAsync("VO-9", Shop);
        await AddShopContactAsync(Shop, "+263771111111");
        await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 5009;
            row.Trigger = CustomerDocumentDeliveryTrigger.Manual;
            row.RecipientE164 = "+263771111111";
        });

        await ScanAsync(Invoice(5009, "VO-9"));

        Assert.DoesNotContain(await DeliveriesAsync(), row => row.Trigger == CustomerDocumentDeliveryTrigger.Auto);
    }

    // ── A person pressing Send ──────────────────────────────────────────

    [Fact]
    public async Task Sending_a_van_invoice_by_hand_offers_the_shops_numbers_and_names_the_shop()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync("VO-10", Shop);
        var contact = await AddShopContactAsync(Shop, "+263771111111");

        var result = await RequestAsync(Invoice(5010, "VO-10"), new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] });

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(Shop, row.RouteCustomerId);
        Assert.Equal("Mbare Tuck Shop", row.CardName);
    }

    [Fact]
    public async Task Another_shops_number_is_refused_for_a_van_invoice()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync("VO-11", Shop);
        var elsewhere = await AddShopContactAsync(OtherShop, "+263772222222");

        var result = await RequestAsync(Invoice(5011, "VO-11"), new RequestInvoiceWhatsAppRequest { ContactIds = [elsewhere.Id] });

        Assert.Equal("CustomerDocuments.ContactNotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task A_number_typed_for_a_van_invoice_is_saved_on_the_shop_not_refused_as_a_selling_account()
    {
        await _kit.SetRuntimeAsync();
        await AddSaleAsync("VO-12", Shop);
        await using (var context = _kit.NewContext())
        {
            // The van's card is a selling account: a number may never be saved on it.
            context.Users.Add(new User { Id = Guid.NewGuid(), Username = "rep", PasswordHash = "x", Role = "Sales", IsActive = true, AssignedBusinessPartnerCode = VanCard });
            await context.SaveChangesAsync();
        }

        var result = await RequestAsync(Invoice(5012, "VO-12"), new RequestInvoiceWhatsAppRequest
        {
            OneOffPhone = "0773333333",
            ConsentAffirmed = true,
            SaveAsContact = true,
            AutoSendFutureInvoices = true
        });

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        await using var check = _kit.NewContext();
        var saved = Assert.Single(check.CustomerWhatsAppContacts);
        Assert.Equal(Shop, saved.RouteCustomerId);
        Assert.Null(saved.CardCode);
        Assert.Equal(Shop, Assert.Single(check.CustomerDocumentDeliveries).RouteCustomerId);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Invoice Invoice(int docEntry, string reference) => new()
    {
        DocEntry = docEntry,
        DocNum = 780000 + docEntry,
        DocDate = "2026-10-09T00:00:00Z",
        CardCode = VanCard,
        CardName = "Van Sales West 2",
        DocTotal = 48.30m,
        DocCurrency = "USD",
        Cancelled = "tNO",
        CancelStatus = "csNo",
        U_Van_saleorder = reference
    };

    private async Task<RouteCustomerInvoiceLink?> ResolveAsync(Invoice invoice)
    {
        await using var context = _kit.NewContext();
        var links = await RouteCustomerInvoiceResolver.ResolveAsync(context, [invoice], NullLogger.Instance, CancellationToken.None);
        return links.GetValueOrDefault(invoice.DocEntry);
    }

    private async Task<int> AddSaleAsync(string reference, int shop, int? sapDocEntry = null, string cardCode = VanCard, string source = SaleSourceSystems.VanSales)
    {
        await using var context = _kit.NewContext();
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = cardCode,
            RouteCustomerId = shop,
            RouteCustomerCode = shop == Shop ? "S41" : "S42",
            RouteCustomerName = shop == Shop ? "Mbare Tuck Shop" : "Sakubva Store",
            DocDate = Now.Date,
            TotalAmount = 48.30m,
            Currency = "USD",
            WarehouseCode = "VAN004",
            SapDocEntry = sapDocEntry
        };
        context.DesktopSales.Add(sale);
        await context.SaveChangesAsync();
        return sale.Id;
    }

    private async Task AddReservationAsync(string reference, int shop, string status)
    {
        await using var context = _kit.NewContext();
        context.StockReservations.Add(new StockReservationEntity
        {
            ExternalReferenceId = reference,
            CardCode = VanCard,
            RouteCustomerId = shop,
            RouteCustomerName = shop == Shop ? "Mbare Tuck Shop" : "Sakubva Store",
            Status = status,
            CreatedAt = Now,
            ExpiresAt = Now.AddHours(1)
        });
        await context.SaveChangesAsync();
    }

    private async Task<CustomerWhatsAppContactEntity> AddShopContactAsync(int shop, string phone)
    {
        await using var context = _kit.NewContext();
        var contact = new CustomerWhatsAppContactEntity
        {
            RouteCustomerId = shop,
            OwnerName = shop == Shop ? "Mbare Tuck Shop" : "Sakubva Store",
            PhoneE164 = phone,
            AutoSendInvoices = true,
            ConsentSource = WhatsAppConsentSource.Web,
            ConsentRecordedAtUtc = Now.AddDays(-3),
            ConsentRecordedBy = "Tendai Moyo",
            WhatsAppExists = true,
            WhatsAppCheckedAtUtc = Now.AddDays(-1)
        };
        context.CustomerWhatsAppContacts.Add(contact);
        await context.SaveChangesAsync();
        return contact;
    }

    private async Task SetCheckpointAsync(int lastDocEntry)
    {
        await using var context = _kit.NewContext();
        await new InvoiceScanCheckpoint { LastDocEntry = lastDocEntry, InitialisedAtUtc = Now.AddDays(-1), LastScanAtUtc = Now.AddMinutes(-2) }
            .StageAsync(context, CancellationToken.None);
        await context.SaveChangesAsync();
    }

    private async Task ScanAsync(params Invoice[] invoices)
    {
        var sap = SapAnswers.Create(new()
        {
            ["GetInvoiceDeliveryHeadersAfterDocEntryAsync"] = args =>
            {
                var after = (int)args![0]!;
                return Task.FromResult(invoices.Where(invoice => invoice.DocEntry > after).OrderBy(invoice => invoice.DocEntry).ToList());
            }
        });

        await using var context = _kit.NewContext();
        var handler = new ScanNewInvoicesForDeliveryHandler(
            context,
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(CustomerDocumentTestKit.Settings()),
            Options.Create(new FiscalisationSettings()),
            _trigger,
            new FakePacer(Now),
            NullLogger<ScanNewInvoicesForDeliveryHandler>.Instance);

        var result = await handler.Handle(new ScanNewInvoicesForDeliveryCommand(), CancellationToken.None);
        Assert.False(result.IsError);
    }

    private async Task<ErrorOr<List<CustomerDocumentDeliveryDto>>> RequestAsync(Invoice invoice, RequestInvoiceWhatsAppRequest request)
    {
        var sap = SapAnswers.Create(new()
        {
            ["GetInvoiceDeliveryHeadersAsync"] = _ => Task.FromResult(new List<Invoice> { invoice })
        });

        await using var context = _kit.NewContext();
        var handler = new RequestInvoiceWhatsAppHandler(
            context,
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(CustomerDocumentTestKit.Settings()),
            Options.Create(new FiscalisationSettings()),
            _trigger,
            _audit,
            NullLogger<RequestInvoiceWhatsAppHandler>.Instance);

        return await handler.Handle(new RequestInvoiceWhatsAppCommand(invoice.DocEntry, request, CustomerDocumentTestKit.Cashier), CancellationToken.None);
    }

    private async Task<List<CustomerDocumentDeliveryEntity>> DeliveriesAsync()
    {
        await using var context = _kit.NewContext();
        return await context.CustomerDocumentDeliveries.AsNoTracking().ToListAsync();
    }
}
