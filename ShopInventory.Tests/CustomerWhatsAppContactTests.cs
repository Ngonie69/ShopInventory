using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.OptOutCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.RemoveCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.SaveCustomerWhatsAppContact;
using ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The register of customers' WhatsApp numbers: saved only with consent, never on an account a till or
/// van sells as, and an opt-out that follows the number wherever it is saved.
/// </summary>
public sealed class CustomerWhatsAppContactTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();
    private readonly RecordingAuditService _audit = new();

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData("0771234567")]
    [InlineData("+263 77 123 4567")]
    [InlineData("263771234567")]
    public async Task A_number_written_any_usual_way_is_saved_as_one_number(string written)
    {
        var result = await SaveAsync(Request(phone: written));

        Assert.False(result.IsError);
        Assert.Equal("+263771234567", Assert.Single(result.Value).PhoneE164);
    }

    [Fact]
    public async Task Consent_is_required()
    {
        var result = await SaveAsync(Request(consent: false));

        Assert.Equal("CustomerDocuments.ConsentRequired", result.FirstError.Code);
        await using var context = _kit.NewContext();
        Assert.Empty(context.CustomerWhatsAppContacts);
    }

    [Fact]
    public async Task Something_that_is_not_a_number_is_refused()
    {
        var result = await SaveAsync(Request(phone: "call me"));

        Assert.Equal("CustomerDocuments.InvalidPhone", result.FirstError.Code);
    }

    [Fact]
    public async Task The_account_a_till_sells_as_cannot_hold_a_number()
    {
        await using (var context = _kit.NewContext())
        {
            context.Shops.Add(new ShopEntity { Code = "FAC", Name = "Factory shop", BusinessPartnerCode = "CIS006", WarehouseCode = "FAC" });
            await context.SaveChangesAsync();
        }

        var result = await SaveAsync(Request(cardCode: "CIS006"));

        Assert.Equal("CustomerDocuments.SellingAccountNotAllowed", result.FirstError.Code);
    }

    [Fact]
    public async Task The_account_a_van_sells_as_cannot_hold_a_number()
    {
        await using (var context = _kit.NewContext())
        {
            var cashier = await context.Users.SingleAsync();
            cashier.AssignedBusinessPartnerCode = "VAN008";
            await context.SaveChangesAsync();
        }

        var result = await SaveAsync(Request(cardCode: "VAN008"));

        Assert.Equal("CustomerDocuments.SellingAccountNotAllowed", result.FirstError.Code);
    }

    [Fact]
    public async Task A_customer_SAP_does_not_know_is_refused()
    {
        var result = await SaveAsync(Request(cardCode: "NOPE01"), partners: []);

        Assert.Equal("CustomerDocuments.UnknownCustomer", result.FirstError.Code);
    }

    [Fact]
    public async Task The_same_shops_other_currency_cards_are_saved_together_under_their_SAP_names()
    {
        var result = await SaveAsync(
            Request(alsoApplyTo: ["SPA050 USD", "SPA070"]),
            partners:
            [
                new BusinessPartnerDto { CardCode = "SPA002", CardName = "Spar Bridge" },
                new BusinessPartnerDto { CardCode = "SPA050 USD", CardName = "Spar Bridge USD" },
                new BusinessPartnerDto { CardCode = "SPA070", CardName = "Spar Bridge ZiG" }
            ]);

        Assert.False(result.IsError);
        Assert.Equal(["SPA002", "SPA050 USD", "SPA070"], result.Value.Select(contact => contact.CardCode ?? string.Empty).ToArray());
        Assert.Equal("Spar Bridge ZiG", result.Value[2].OwnerName);
        Assert.All(result.Value, contact => Assert.Equal("Tendai Moyo", contact.ConsentRecordedBy));
        Assert.Equal(3, _audit.Entries.Count(entry => entry.Action == AuditActions.SaveCustomerWhatsAppContact));
    }

    [Fact]
    public async Task One_unknown_sibling_saves_none_of_them()
    {
        var result = await SaveAsync(
            Request(alsoApplyTo: ["SPA999"]),
            partners: [new BusinessPartnerDto { CardCode = "SPA002", CardName = "Spar Bridge" }]);

        Assert.True(result.IsError);
        await using var context = _kit.NewContext();
        Assert.Empty(context.CustomerWhatsAppContacts);
    }

    [Fact]
    public async Task A_customer_may_have_only_so_many_numbers()
    {
        var settings = CustomerDocumentTestKit.Settings(change => change.MaxContactsPerOwner = 2);
        await SaveAsync(Request(phone: "0771111111"), settings: settings);
        await SaveAsync(Request(phone: "0772222222"), settings: settings);

        var third = await SaveAsync(Request(phone: "0773333333"), settings: settings);

        Assert.Equal("CustomerDocuments.ContactLimitReached", third.FirstError.Code);
    }

    [Fact]
    public async Task Saving_a_number_again_renews_its_consent_instead_of_adding_a_second()
    {
        var first = await SaveAsync(Request(note: "Asked by phone"));
        var second = await SaveAsync(Request(note: "Signed the account form"));

        Assert.Equal(first.Value[0].Id, second.Value[0].Id);
        Assert.Equal("Signed the account form", second.Value[0].ConsentNote);
        await using var context = _kit.NewContext();
        Assert.Single(context.CustomerWhatsAppContacts);
    }

    [Fact]
    public async Task Opting_out_follows_the_number_to_every_customer_and_withdraws_what_was_waiting()
    {
        var onSpar = await _kit.AddContactAsync(cardCode: "SPA002");
        var onSparZig = await _kit.AddContactAsync(cardCode: "SPA070");
        var waiting = await _kit.AddDeliveryAsync(row => row.ContactId = onSparZig.Id);

        await using (var context = _kit.NewContext())
        {
            var result = await new OptOutCustomerWhatsAppContactHandler(context, _audit, NullLogger<OptOutCustomerWhatsAppContactHandler>.Instance)
                .Handle(new OptOutCustomerWhatsAppContactCommand(onSpar.Id, CustomerDocumentTestKit.Cashier), CancellationToken.None);

            Assert.False(result.IsError);
            Assert.All(result.Value, contact => Assert.True(contact.IsOptedOut));
        }

        await using var verify = _kit.NewContext();
        Assert.All(await verify.CustomerWhatsAppContacts.ToListAsync(), contact =>
        {
            Assert.NotNull(contact.OptedOutAtUtc);
            Assert.Equal(WhatsAppOptOutSource.Web, contact.OptedOutSource);
        });
        Assert.Equal(CustomerDocumentDeliveryStatus.Cancelled, (await _kit.ReloadDeliveryAsync(waiting.Id)).Status);
    }

    [Fact]
    public async Task Fresh_consent_lifts_an_opt_out_on_that_customer()
    {
        await _kit.AddContactAsync(optedOutAtUtc: DateTime.UtcNow.AddDays(-3));

        var result = await SaveAsync(Request(note: "Asked to receive invoices again"));

        Assert.False(result.IsError);
        Assert.False(Assert.Single(result.Value).IsOptedOut);
    }

    [Fact]
    public async Task Removing_a_number_keeps_the_record_and_withdraws_its_waiting_sends()
    {
        var contact = await _kit.AddContactAsync();
        var waiting = await _kit.AddDeliveryAsync(row => row.ContactId = contact.Id);

        await using (var context = _kit.NewContext())
        {
            var result = await new RemoveCustomerWhatsAppContactHandler(context, _audit, NullLogger<RemoveCustomerWhatsAppContactHandler>.Instance)
                .Handle(new RemoveCustomerWhatsAppContactCommand(contact.Id, CustomerDocumentTestKit.Cashier), CancellationToken.None);
            Assert.False(result.IsError);
        }

        await using var verify = _kit.NewContext();
        var stored = await verify.CustomerWhatsAppContacts.SingleAsync();
        Assert.NotNull(stored.RemovedAtUtc);
        Assert.Equal("Tendai Moyo", stored.RemovedBy);
        Assert.Equal(CustomerDocumentDeliveryStatus.Cancelled, (await _kit.ReloadDeliveryAsync(waiting.Id)).Status);
    }

    [Fact]
    public async Task Turning_automatic_sending_off_withdraws_the_automatic_sends_already_waiting()
    {
        var contact = await _kit.AddContactAsync(autoSend: true);
        var automatic = await _kit.AddDeliveryAsync(row =>
        {
            row.ContactId = contact.Id;
            row.Trigger = CustomerDocumentDeliveryTrigger.Auto;
        });
        var manual = await _kit.AddDeliveryAsync(row =>
        {
            row.ContactId = contact.Id;
            row.SapDocEntry = 2400101;
        });

        await using (var context = _kit.NewContext())
        {
            var result = await new UpdateCustomerWhatsAppContactHandler(context, _audit, NullLogger<UpdateCustomerWhatsAppContactHandler>.Instance)
                .Handle(new UpdateCustomerWhatsAppContactCommand(
                    contact.Id,
                    new UpdateCustomerWhatsAppContactRequest { ContactName = "Accounts", AutoSendInvoices = false },
                    CustomerDocumentTestKit.Cashier), CancellationToken.None);
            Assert.False(result.IsError);
            Assert.Equal("Accounts", result.Value.ContactName);
        }

        Assert.Equal(CustomerDocumentDeliveryStatus.Cancelled, (await _kit.ReloadDeliveryAsync(automatic.Id)).Status);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(manual.Id)).Status);
    }

    [Fact]
    public async Task The_database_refuses_a_contact_with_two_owners()
    {
        await using var context = _kit.NewContext();
        context.RouteCustomers.Add(new RouteCustomerEntity { AssignedBusinessPartnerCode = "VAN008", Code = "S1", Name = "Tuck shop" });
        await context.SaveChangesAsync();

        context.CustomerWhatsAppContacts.Add(new CustomerWhatsAppContactEntity
        {
            CardCode = "SPA002",
            RouteCustomerId = context.RouteCustomers.Single().Id,
            OwnerName = "Both",
            PhoneE164 = "+263771234567",
            ConsentRecordedBy = "x"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static SaveCustomerWhatsAppContactRequest Request(
        string phone = "0771234567",
        string cardCode = CustomerDocumentTestKit.CardCode,
        bool consent = true,
        string? note = "Asked at the counter",
        List<string>? alsoApplyTo = null) => new()
        {
            CardCode = cardCode,
            Phone = phone,
            ContactName = "Buyer",
            AutoSendInvoices = true,
            ConsentConfirmed = consent,
            ConsentNote = note,
            AlsoApplyToCardCodes = alsoApplyTo
        };

    private async Task<ErrorOr<List<CustomerWhatsAppContactDto>>> SaveAsync(
        SaveCustomerWhatsAppContactRequest request,
        List<BusinessPartnerDto>? partners = null,
        CustomerDocumentDeliverySettings? settings = null)
    {
        var known = partners ?? [new BusinessPartnerDto { CardCode = request.CardCode, CardName = "Spar Bridge" }];
        var sap = SapAnswers.Create(new()
        {
            ["GetBusinessPartnersByCodesAsync"] = args =>
            {
                var codes = (IReadOnlyCollection<string>)args![0]!;
                return Task.FromResult(known.Where(partner => codes.Contains(partner.CardCode!, StringComparer.OrdinalIgnoreCase)).ToList());
            }
        });

        await using var context = _kit.NewContext();
        var handler = new SaveCustomerWhatsAppContactHandler(
            context,
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(settings ?? CustomerDocumentTestKit.Settings()),
            _audit,
            NullLogger<SaveCustomerWhatsAppContactHandler>.Instance);

        return await handler.Handle(new SaveCustomerWhatsAppContactCommand(request, CustomerDocumentTestKit.Cashier), CancellationToken.None);
    }
}
