using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactRegister;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The page that lists every customer's WhatsApp number: found by any run of its digits, filtered by
/// state, counted whatever the filters, and showing what has been sent to each.
/// </summary>
public sealed class CustomerWhatsAppContactRegisterTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData("1567")]
    [InlineData("0771 231 567")]
    [InlineData("+263 77 123 1567")]
    public async Task A_number_is_found_by_its_last_digits_or_however_it_was_typed(string search)
    {
        await _kit.AddContactAsync(phone: "+263771231567");
        await _kit.AddContactAsync(phone: "+263772000000");

        var page = await ReadAsync(search: search);

        Assert.Equal("+263771231567", Assert.Single(page.Items).Contact.PhoneE164);
    }

    [Fact]
    public async Task A_number_is_found_by_who_recorded_it()
    {
        await _kit.AddContactAsync(phone: "+263771231567");

        var page = await ReadAsync(search: "tendai");

        Assert.Equal("Tendai Moyo", Assert.Single(page.Items).Contact.ConsentRecordedBy);
    }

    [Fact]
    public async Task Each_state_filters_and_the_counts_ignore_the_filters()
    {
        await _kit.AddContactAsync(phone: "+263771000001", autoSend: true);
        await _kit.AddContactAsync(phone: "+263771000002", autoSend: false, whatsAppExists: false);
        await _kit.AddContactAsync(phone: "+263771000003", optedOutAtUtc: DateTime.UtcNow);
        var removed = await _kit.AddContactAsync(phone: "+263771000004");
        await using (var context = _kit.NewContext())
        {
            var row = await context.CustomerWhatsAppContacts.FindAsync(removed.Id);
            row!.RemovedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        Assert.Equal(3, (await ReadAsync()).TotalCount);
        Assert.Equal("+263771000001", Assert.Single((await ReadAsync(state: "auto")).Items).Contact.PhoneE164);
        Assert.Equal("+263771000002", Assert.Single((await ReadAsync(state: "request")).Items).Contact.PhoneE164);
        Assert.Equal("+263771000002", Assert.Single((await ReadAsync(state: "notonwhatsapp")).Items).Contact.PhoneE164);
        Assert.Equal("+263771000003", Assert.Single((await ReadAsync(state: "optedout")).Items).Contact.PhoneE164);
        Assert.Equal("+263771000004", Assert.Single((await ReadAsync(state: "removed")).Items).Contact.PhoneE164);

        var summary = (await ReadAsync(state: "auto")).Summary;
        Assert.Equal(3, summary.Linked);
        Assert.Equal(1, summary.Automatic);
        Assert.Equal(1, summary.OnRequest);
        Assert.Equal(1, summary.OptedOut);
        Assert.Equal(1, summary.NotOnWhatsApp);
        Assert.Equal(1, summary.Removed);
    }

    [Fact]
    public async Task Each_number_carries_what_was_sent_to_it_and_what_waits()
    {
        var contact = await _kit.AddContactAsync(phone: "+263771231567");
        var sentAt = DateTime.UtcNow.AddHours(-2);
        await _kit.AddDeliveryAsync(row =>
        {
            row.ContactId = contact.Id;
            row.Status = CustomerDocumentDeliveryStatus.Sent;
            row.SentAtUtc = sentAt;
        });
        await _kit.AddDeliveryAsync(row =>
        {
            row.ContactId = contact.Id;
            row.SapDocEntry = 2400101;
            row.Status = CustomerDocumentDeliveryStatus.WaitingForFiscal;
        });
        await _kit.AddDeliveryAsync(row =>
        {
            row.ContactId = contact.Id;
            row.SapDocEntry = 2400102;
            row.Status = CustomerDocumentDeliveryStatus.Failed;
        });

        var item = Assert.Single((await ReadAsync()).Items);

        Assert.Equal(1, item.SentCount);
        Assert.Equal(1, item.WaitingCount);
        Assert.Equal(sentAt, item.LastSentAtUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task One_number_is_read_by_its_id_whatever_its_state()
    {
        var contact = await _kit.AddContactAsync(phone: "+263771231567", optedOutAtUtc: DateTime.UtcNow);
        await _kit.AddContactAsync(phone: "+263772000000");

        var page = await ReadAsync(contactId: contact.Id);

        Assert.Equal(contact.Id, Assert.Single(page.Items).Contact.Id);
    }

    [Theory]
    [InlineData("Spar", null)]
    [InlineData("12", null)]
    [InlineData("1567", "1567")]
    [InlineData("0771 231 567", "771231567")]
    public void Only_a_number_search_looks_at_the_digits(string search, string? expected)
    {
        Assert.Equal(expected, GetCustomerWhatsAppContactRegisterHandler.PhoneDigits(search));
    }

    private async Task<CustomerWhatsAppContactRegisterDto> ReadAsync(
        string? search = null,
        string? state = null,
        int? contactId = null)
    {
        await using var context = _kit.NewContext();
        var result = await new GetCustomerWhatsAppContactRegisterHandler(context).Handle(
            new GetCustomerWhatsAppContactRegisterQuery(search, state, Source: null, contactId),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }
}
