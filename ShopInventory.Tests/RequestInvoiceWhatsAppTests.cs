using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.CancelCustomerDocumentDelivery;
using ShopInventory.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;
using ShopInventory.Features.CustomerDocuments.Commands.RetryCustomerDocumentDelivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Asking for an invoice to be sent: nothing is sent here, but this is where a wrong document or a
/// wrong number is stopped before it is queued.
/// </summary>
public sealed class RequestInvoiceWhatsAppTests : IDisposable
{
    private const int DocEntry = 2400100;

    private readonly CustomerDocumentTestKit _kit = new();
    private readonly RecordingAuditService _audit = new();
    private readonly RecordingDispatchTrigger _trigger = new();
    private readonly FakeOpenWAClient _gateway = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task A_saved_number_is_queued_with_the_invoice_copied_onto_it_and_the_job_woken()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync();

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] });

        var queued = Assert.Single(result.Value);
        Assert.Equal("Pending", queued.Status);
        Assert.Equal("Manual", queued.Trigger);
        Assert.Equal("780100", queued.DocumentNumber);
        Assert.Equal("*** *** 4567", queued.RecipientMasked);
        Assert.Equal(1, _trigger.Triggered);

        var stored = await _kit.ReloadDeliveryAsync(queued.Id);
        Assert.Equal(CustomerDocumentTestKit.CardCode, stored.CardCode);
        Assert.Equal("KEF-WEB-1", stored.SaleReference);
        Assert.Equal(125.50m, stored.DocumentTotal);
        Assert.Equal(contact.Id, stored.ContactId);
        Assert.Contains(AuditActions.RequestDocumentWhatsApp, _audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task With_no_session_chosen_the_gateways_ready_one_is_used_and_the_send_is_queued()
    {
        // No settings saved at all: nobody has opened WhatsApp Deliveries.
        var contact = await _kit.AddContactAsync();

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] });

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Single(result.Value);
        Assert.Equal(CustomerDocumentTestKit.SessionId, await _kit.SavedSessionIdAsync());
    }

    [Fact]
    public async Task A_send_is_refused_when_the_gateway_has_no_number_to_send_from()
    {
        var contact = await _kit.AddContactAsync();
        _gateway.SessionStatus = "disconnected";

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] });

        Assert.Equal("CustomerDocuments.SessionNotConfigured", result.FirstError.Code);
        Assert.Contains("No WhatsApp number is connected", result.FirstError.Description);
        Assert.Null(await _kit.SavedSessionIdAsync());
    }

    [Fact]
    public async Task A_send_is_refused_while_an_administrator_has_stopped_sending()
    {
        await _kit.SetRuntimeAsync(sessionId: null, stopped: true);
        var contact = await _kit.AddContactAsync();

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] });

        Assert.Equal("CustomerDocuments.SendingStopped", result.FirstError.Code);
        Assert.Null(await _kit.SavedSessionIdAsync());
    }

    [Fact]
    public async Task A_node_that_cannot_ask_the_gateway_queues_for_the_node_that_sends()
    {
        var contact = await _kit.AddContactAsync();

        var result = await RequestAsync(
            new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] },
            openWa: CustomerDocumentTestKit.Gateway(configured: false));

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Single(result.Value);
        Assert.Null(await _kit.SavedSessionIdAsync());
    }

    [Theory]
    [InlineData("tYES", "csYes", null, null, "CustomerDocuments.InvoiceCancelled")]
    [InlineData("tNO", "csCancellation", null, null, "CustomerDocuments.InvoiceCancelled")]
    [InlineData("tNO", "csNo", "CONSOL-20261007-SPA002", null, "CustomerDocuments.ConsolidatedNotSendable")]
    [InlineData("tNO", "csNo", "KEF-WEB-1", "Invoice posted from SAP update. Old invoice 771234.", "CustomerDocuments.RepostedNotSendable")]
    public async Task An_invoice_that_is_not_the_customers_to_receive_is_refused(
        string cancelled, string cancelStatus, string? saleReference, string? comments, string expected)
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync();

        var result = await RequestAsync(
            new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] },
            invoice: Invoice(cancelled, cancelStatus, saleReference, comments));

        Assert.Equal(expected, result.FirstError.Code);
        await using var context = _kit.NewContext();
        Assert.Empty(context.CustomerDocumentDeliveries);
    }

    [Fact]
    public async Task A_number_saved_on_another_customer_is_refused()
    {
        await _kit.SetRuntimeAsync();
        var elsewhere = await _kit.AddContactAsync(cardCode: "CHE012");

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { ContactIds = [elsewhere.Id] });

        Assert.Equal("CustomerDocuments.ContactNotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task An_opted_out_number_is_refused()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync(optedOutAtUtc: DateTime.UtcNow.AddDays(-1));

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { ContactIds = [contact.Id] });

        Assert.Equal("CustomerDocuments.NumberOptedOut", result.FirstError.Code);
    }

    [Fact]
    public async Task A_one_off_number_needs_the_senders_word_that_the_customer_asked_for_it()
    {
        await _kit.SetRuntimeAsync();

        var refused = await RequestAsync(new RequestInvoiceWhatsAppRequest { OneOffPhone = "0779876543" });
        var accepted = await RequestAsync(new RequestInvoiceWhatsAppRequest { OneOffPhone = "0779876543", ConsentAffirmed = true });

        Assert.Equal("CustomerDocuments.ConsentRequired", refused.FirstError.Code);
        var queued = Assert.Single(accepted.Value);
        Assert.Null(queued.ContactId);
        Assert.True((await _kit.ReloadDeliveryAsync(queued.Id)).ConsentAffirmed);
    }

    [Fact]
    public async Task A_one_off_number_someone_opted_out_is_refused()
    {
        await _kit.SetRuntimeAsync();
        await _kit.AddContactAsync(phone: "+263779876543", cardCode: "CHE012", optedOutAtUtc: DateTime.UtcNow.AddDays(-1));

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest { OneOffPhone = "0779876543", ConsentAffirmed = true });

        Assert.Equal("CustomerDocuments.NumberOptedOut", result.FirstError.Code);
    }

    [Fact]
    public async Task One_off_numbers_are_capped_per_person_per_day()
    {
        await _kit.SetRuntimeAsync();
        var settings = CustomerDocumentTestKit.Settings(change => change.MaxOneOffPerUserPerDay = 1);
        await RequestAsync(new RequestInvoiceWhatsAppRequest { OneOffPhone = "0779876543", ConsentAffirmed = true }, settings: settings);

        var second = await RequestAsync(new RequestInvoiceWhatsAppRequest { OneOffPhone = "0779876544", ConsentAffirmed = true }, settings: settings);

        Assert.Equal("CustomerDocuments.OneOffLimitReached", second.FirstError.Code);
    }

    [Fact]
    public async Task A_one_off_number_can_be_kept_on_the_customer_for_next_time()
    {
        await _kit.SetRuntimeAsync();

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest
        {
            OneOffPhone = "0779876543",
            OneOffName = "Accounts clerk",
            ConsentAffirmed = true,
            SaveAsContact = true,
            AutoSendFutureInvoices = true
        });

        var queued = Assert.Single(result.Value);
        await using var context = _kit.NewContext();
        var saved = await context.CustomerWhatsAppContacts.SingleAsync();
        Assert.Equal(saved.Id, queued.ContactId);
        Assert.Equal(CustomerDocumentTestKit.CardCode, saved.CardCode);
        Assert.Equal("+263779876543", saved.PhoneE164);
        Assert.True(saved.AutoSendInvoices);
        Assert.Equal("Given for invoice 780100", saved.ConsentNote);
    }

    [Fact]
    public async Task The_same_number_chosen_twice_is_queued_once()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync();

        var result = await RequestAsync(new RequestInvoiceWhatsAppRequest
        {
            ContactIds = [contact.Id],
            OneOffPhone = "0771234567",
            ConsentAffirmed = true
        });

        Assert.Single(result.Value);
    }

    // ── Resend and withdraw ─────────────────────────────────────────────

    [Fact]
    public async Task A_document_that_may_have_arrived_is_resent_only_on_the_senders_word()
    {
        var sent = await _kit.AddDeliveryAsync(row =>
        {
            row.Status = CustomerDocumentDeliveryStatus.SentUnconfirmed;
            row.SendIssuedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        });

        var refused = await RetryAsync(sent.Id, confirmNotReceived: false);
        var resent = await RetryAsync(sent.Id, confirmNotReceived: true);

        Assert.Equal("CustomerDocuments.ReceiptNotConfirmed", refused.FirstError.Code);
        Assert.Equal(sent.Id, resent.Value.SupersedesDeliveryId);
        Assert.Equal("Pending", resent.Value.Status);
        Assert.Equal(CustomerDocumentDeliveryStatus.SentUnconfirmed, (await _kit.ReloadDeliveryAsync(sent.Id)).Status);
    }

    [Fact]
    public async Task A_document_still_with_the_job_cannot_be_resent()
    {
        var pending = await _kit.AddDeliveryAsync();

        var result = await RetryAsync(pending.Id, confirmNotReceived: true);

        Assert.Equal("CustomerDocuments.DeliveryNotRetryable", result.FirstError.Code);
    }

    [Fact]
    public async Task Resending_a_held_document_closes_the_held_one()
    {
        var held = await _kit.AddDeliveryAsync(row => row.Status = CustomerDocumentDeliveryStatus.Held);

        var resent = await RetryAsync(held.Id, confirmNotReceived: false);

        Assert.False(resent.IsError);
        Assert.Equal(CustomerDocumentDeliveryStatus.Cancelled, (await _kit.ReloadDeliveryAsync(held.Id)).Status);
    }

    [Fact]
    public async Task A_waiting_document_can_be_withdrawn_but_one_being_sent_cannot()
    {
        var waiting = await _kit.AddDeliveryAsync();
        var sending = await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400101;
            row.Status = CustomerDocumentDeliveryStatus.Sending;
        });

        var withdrawn = await CancelAsync(waiting.Id);
        var refused = await CancelAsync(sending.Id);

        Assert.Equal("Cancelled", withdrawn.Value.Status);
        Assert.Equal("CustomerDocuments.DeliveryNotCancellable", refused.FirstError.Code);
        Assert.Equal(CustomerDocumentDeliveryStatus.Sending, (await _kit.ReloadDeliveryAsync(sending.Id)).Status);
    }

    private static Invoice Invoice(
        string cancelled = "tNO",
        string cancelStatus = "csNo",
        string? saleReference = "KEF-WEB-1",
        string? comments = null) => new()
        {
            DocEntry = DocEntry,
            DocNum = 780100,
            DocDate = "2026-10-07T00:00:00Z",
            CardCode = CustomerDocumentTestKit.CardCode,
            CardName = "Spar Bridge",
            DocTotal = 125.50m,
            DocCurrency = "USD",
            Cancelled = cancelled,
            CancelStatus = cancelStatus,
            U_Van_saleorder = saleReference,
            Comments = comments
        };

    private async Task<ErrorOr<List<CustomerDocumentDeliveryDto>>> RequestAsync(
        RequestInvoiceWhatsAppRequest request,
        Invoice? invoice = null,
        CustomerDocumentDeliverySettings? settings = null,
        OpenWASettings? openWa = null)
    {
        var header = invoice ?? Invoice();
        var sap = SapAnswers.Create(new()
        {
            ["GetInvoiceDeliveryHeadersAsync"] = _ => Task.FromResult(new List<Invoice> { header })
        });

        await using var context = _kit.NewContext();
        var handler = new RequestInvoiceWhatsAppHandler(
            context,
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(settings ?? CustomerDocumentTestKit.Settings()),
            Options.Create(new FiscalisationSettings { RepostedInvoiceCommentsPrefix = "Invoice posted from SAP update." }),
            _gateway,
            Options.Create(openWa ?? CustomerDocumentTestKit.Gateway()),
            _trigger,
            _audit,
            NullLogger<RequestInvoiceWhatsAppHandler>.Instance);

        return await handler.Handle(new RequestInvoiceWhatsAppCommand(DocEntry, request, CustomerDocumentTestKit.Cashier), CancellationToken.None);
    }

    private async Task<ErrorOr<CustomerDocumentDeliveryDto>> RetryAsync(long id, bool confirmNotReceived)
    {
        await using var context = _kit.NewContext();
        var handler = new RetryCustomerDocumentDeliveryHandler(
            context,
            Options.Create(CustomerDocumentTestKit.Settings()),
            _trigger,
            _audit,
            NullLogger<RetryCustomerDocumentDeliveryHandler>.Instance);

        return await handler.Handle(
            new RetryCustomerDocumentDeliveryCommand(id, new RetryCustomerDocumentDeliveryRequest { ConfirmNotReceived = confirmNotReceived }, CustomerDocumentTestKit.Cashier),
            CancellationToken.None);
    }

    private async Task<ErrorOr<CustomerDocumentDeliveryDto>> CancelAsync(long id)
    {
        await using var context = _kit.NewContext();
        var handler = new CancelCustomerDocumentDeliveryHandler(context, _audit, NullLogger<CancelCustomerDocumentDeliveryHandler>.Instance);
        return await handler.Handle(new CancelCustomerDocumentDeliveryCommand(id, CustomerDocumentTestKit.Cashier), CancellationToken.None);
    }
}
