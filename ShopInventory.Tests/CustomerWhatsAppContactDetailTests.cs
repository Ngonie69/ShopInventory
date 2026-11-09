using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactDetail;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// One saved number in full, as the contacts page's drawer shows it: who first added it — which a
/// later save must not overwrite — and a history pieced from the audit trail and the row itself.
/// </summary>
public sealed class CustomerWhatsAppContactDetailTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task A_number_saved_on_a_van_handset_names_the_rep_who_saved_it()
    {
        var contact = await AddAsync(WhatsAppConsentSource.VanHandset, "Rumbi Chikwanha");

        var detail = await ReadAsync(contact.Id);

        Assert.Equal("Rumbi Chikwanha", detail.AddedBy);
        Assert.Equal("VanHandset", detail.AddedVia);
        var saved = Assert.Single(detail.History, entry => entry.Kind == "Saved");
        Assert.Equal("Saved on Spar Bridge", saved.What);
    }

    [Fact]
    public async Task The_first_web_save_stays_the_adder_after_someone_saves_it_again()
    {
        var created = DateTime.UtcNow.AddDays(-10);
        var contact = await AddAsync(WhatsAppConsentSource.Web, "Farai Ncube", created, consentAtUtc: DateTime.UtcNow.AddDays(-1));
        await AuditAsync(contact.Id, created.AddSeconds(1), "cashier", AuditActions.SaveCustomerWhatsAppContact,
            "Saved *** *** 4567 on SPA002 (Spar Bridge); automatic invoices on; consent: confirmed");
        await AuditAsync(contact.Id, DateTime.UtcNow.AddDays(-1), "fncube", AuditActions.SaveCustomerWhatsAppContact,
            "Saved *** *** 4567 on SPA002 (Spar Bridge); automatic invoices off; consent: confirmed");

        var detail = await ReadAsync(contact.Id);

        Assert.Equal("Tendai Moyo", detail.AddedBy); // the kit's cashier user, by full name
        Assert.Equal("Farai Ncube", detail.Contact.ConsentRecordedBy);
        Assert.Collection(
            detail.History.Where(entry => entry.Kind == "Saved"),
            latest => Assert.StartsWith("Saved again", latest.What),
            first => Assert.Equal("Saved on Spar Bridge, invoices sent automatically", first.What));
    }

    [Fact]
    public async Task Who_first_added_it_is_not_guessed_when_nothing_recorded_it()
    {
        // Created long before its current consent, and no audit row for the first save.
        var contact = await AddAsync(WhatsAppConsentSource.Till, "Farai Ncube",
            createdAtUtc: DateTime.UtcNow.AddDays(-30), consentAtUtc: DateTime.UtcNow.AddDays(-2));

        var detail = await ReadAsync(contact.Id);

        Assert.Null(detail.AddedBy);
        Assert.Contains(detail.History, entry => entry.Kind == "Saved" && entry.Who == "Farai Ncube" && entry.What.StartsWith("Saved again"));
    }

    [Fact]
    public async Task Changes_opt_outs_and_checks_are_in_the_history_newest_first()
    {
        var created = DateTime.UtcNow.AddDays(-5);
        var contact = await AddAsync(WhatsAppConsentSource.Web, "Tendai Moyo", created);
        await using (var context = _kit.NewContext())
        {
            var row = await context.CustomerWhatsAppContacts.FindAsync(contact.Id);
            row!.OptedOutAtUtc = DateTime.UtcNow.AddHours(-1);
            row.OptedOutSource = WhatsAppOptOutSource.StopKeyword;
            row.WhatsAppExists = false;
            row.WhatsAppCheckedAtUtc = DateTime.UtcNow.AddDays(-3);
            await context.SaveChangesAsync();
        }
        await AuditAsync(contact.Id, DateTime.UtcNow.AddDays(-4), "cashier", AuditActions.SaveCustomerWhatsAppContact,
            "Updated *** *** 4567 on SPA002; automatic invoices off");

        var detail = await ReadAsync(contact.Id);

        Assert.Equal(
            ["OptedOut", "Checked", "Changed", "Saved"],
            detail.History.Select(entry => entry.Kind).ToArray());
        Assert.Equal("StopKeyword", detail.History[0].Via);
        Assert.Equal("WhatsApp check: no WhatsApp account", detail.History[1].What);
        Assert.Equal("Automatic invoices turned off", detail.History[2].What);
        Assert.Equal("Tendai Moyo", detail.History[2].Who);
    }

    [Fact]
    public async Task An_audited_opt_out_is_not_listed_twice()
    {
        var contact = await AddAsync(WhatsAppConsentSource.Web, "Tendai Moyo");
        var optedOutAt = DateTime.UtcNow.AddMinutes(-30);
        await using (var context = _kit.NewContext())
        {
            var row = await context.CustomerWhatsAppContacts.FindAsync(contact.Id);
            row!.OptedOutAtUtc = optedOutAt;
            row.OptedOutSource = WhatsAppOptOutSource.Web;
            row.OptedOutBy = "Tendai Moyo";
            await context.SaveChangesAsync();
        }
        await AuditAsync(contact.Id, optedOutAt.AddSeconds(2), "cashier", AuditActions.OptOutCustomerWhatsAppContact, "Opted out");

        var detail = await ReadAsync(contact.Id);

        Assert.Single(detail.History, entry => entry.Kind == "OptedOut");
    }

    [Fact]
    public async Task Recent_sends_and_counts_come_with_the_number()
    {
        var contact = await AddAsync(WhatsAppConsentSource.Web, "Tendai Moyo");
        for (var i = 0; i < 7; i++)
        {
            var n = i;
            await _kit.AddDeliveryAsync(row =>
            {
                row.ContactId = contact.Id;
                row.SapDocEntry = 2400100 + n;
                row.DocumentNumber = (780100 + n).ToString();
                row.Status = n == 6 ? CustomerDocumentDeliveryStatus.WaitingForFiscal : CustomerDocumentDeliveryStatus.Sent;
                row.SentAtUtc = n == 6 ? null : DateTime.UtcNow.AddHours(-10 + n);
                row.CreatedAtUtc = DateTime.UtcNow.AddHours(-10 + n);
            });
        }

        var detail = await ReadAsync(contact.Id);

        Assert.Equal(6, detail.SentCount);
        Assert.Equal(1, detail.WaitingCount);
        Assert.Equal(5, detail.RecentDeliveries.Count);
        Assert.Equal("780106", detail.RecentDeliveries[0].DocumentNumber);
    }

    [Fact]
    public async Task An_unknown_number_is_not_found()
    {
        await using var context = _kit.NewContext();
        var result = await new GetCustomerWhatsAppContactDetailHandler(context).Handle(
            new GetCustomerWhatsAppContactDetailQuery(424242), CancellationToken.None);

        Assert.Equal("CustomerDocuments.ContactNotFound", result.FirstError.Code);
    }

    private async Task<CustomerWhatsAppContactEntity> AddAsync(
        WhatsAppConsentSource source,
        string recordedBy,
        DateTime? createdAtUtc = null,
        DateTime? consentAtUtc = null)
    {
        var created = createdAtUtc ?? DateTime.UtcNow.AddDays(-3);
        await using var context = _kit.NewContext();
        var contact = new CustomerWhatsAppContactEntity
        {
            CardCode = CustomerDocumentTestKit.CardCode,
            OwnerName = "Spar Bridge",
            PhoneE164 = "+263771234567",
            AutoSendInvoices = true,
            ConsentSource = source,
            ConsentRecordedAtUtc = consentAtUtc ?? created,
            ConsentRecordedBy = recordedBy,
            CreatedAtUtc = created,
            UpdatedAtUtc = consentAtUtc ?? created
        };
        context.CustomerWhatsAppContacts.Add(contact);
        await context.SaveChangesAsync();
        return contact;
    }

    private async Task AuditAsync(int contactId, DateTime atUtc, string username, string action, string details)
    {
        await using var context = _kit.NewContext();
        context.AuditLogs.Add(new AuditLog
        {
            Username = username,
            UserRole = "Cashier",
            Action = action,
            EntityType = "CustomerWhatsAppContact",
            EntityId = contactId.ToString(),
            Details = details,
            IsSuccess = true,
            Timestamp = atUtc
        });
        await context.SaveChangesAsync();
    }

    private async Task<CustomerWhatsAppContactDetailDto> ReadAsync(int contactId)
    {
        await using var context = _kit.NewContext();
        var result = await new GetCustomerWhatsAppContactDetailHandler(context).Handle(
            new GetCustomerWhatsAppContactDetailQuery(contactId), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }
}
