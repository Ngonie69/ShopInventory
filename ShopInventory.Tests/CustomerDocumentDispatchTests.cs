using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Commands.DispatchCustomerDocumentDeliveries;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The delivery pass: the only code that sends a customer document, and so the one place a document
/// could go twice, go to someone who opted out, or go without its fiscal receipt.
/// </summary>
public sealed class CustomerDocumentDispatchTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();
    private readonly FakeOpenWAClient _gateway = new();
    private readonly FakeDocumentComposer _composer = new();
    private readonly RecordingAlerts _alerts = new();
    private readonly FakePacer _pacer = new(DateTime.UtcNow);

    public void Dispose() => _kit.Dispose();

    // ── Sending ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_due_document_is_sent_and_its_message_id_kept()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();

        var result = await PassAsync();

        var sent = Assert.Single(_gateway.Documents);
        Assert.Equal("263771234567@c.us", sent.ChatId);
        Assert.Equal("application/pdf", sent.Mimetype);
        Assert.Equal("Kefalos-Invoice-780100.pdf", sent.Filename);
        Assert.Equal(Convert.ToBase64String(CustomerDocumentTestKit.Document().Bytes), sent.Base64);
        Assert.Equal(1, result.Sent);

        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Sent, stored.Status);
        Assert.StartsWith("true_263771234567@c.us", stored.MessageId);
        Assert.NotNull(stored.SendIssuedAtUtc);
        Assert.NotNull(stored.ClosedAtUtc);
        Assert.Equal("abc", stored.FileSha256);
        Assert.Equal(FiscalLinkVerifier.TransactionLogSource, stored.FiscalEvidenceSource);
        Assert.Equal("ABCD-1234-EF56-7890", stored.FiscalVerificationCode);
        Assert.Null(stored.ClaimToken);
    }

    [Fact]
    public async Task The_row_already_reads_Sending_when_the_gateway_is_called()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        CustomerDocumentDeliveryStatus? seen = null;

        _gateway.OnSend = async request =>
        {
            await using var context = _kit.NewContext();
            seen = (await context.CustomerDocumentDeliveries.AsNoTracking().SingleAsync(row => row.Id == delivery.Id)).Status;
            return new WhatsAppMessageDispatchDto { MessageId = "wamid-1", Timestamp = 1 };
        };

        await PassAsync();

        // Saved before the call, so a process that dies mid-send leaves a row that says so.
        Assert.Equal(CustomerDocumentDeliveryStatus.Sending, seen);
    }

    [Fact]
    public async Task An_unconfirmed_send_is_closed_and_never_sent_again()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _gateway.OnSend = _ => Task.FromResult(new WhatsAppMessageDispatchDto { MessageId = null, Unconfirmed = true, Timestamp = 1 });

        await PassAsync();
        _pacer.Now = _pacer.Now.AddHours(1);
        await PassAsync();

        Assert.Single(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.SentUnconfirmed, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task A_lost_answer_is_uncertain_and_is_not_sent_again_blind()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _gateway.OnSend = _ => Task.FromException<WhatsAppMessageDispatchDto>(
            new TaskCanceledException("OpenWA did not answer", new TimeoutException()));

        await PassAsync();
        _pacer.Now = _pacer.Now.AddMinutes(1);
        await PassAsync();

        Assert.Single(_gateway.Documents);
        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Uncertain, stored.Status);
        Assert.Contains(RecordingAlertsCondition.Uncertain, _alerts.Raised);
    }

    [Theory]
    [InlineData("sent", CustomerDocumentDeliveryStatus.Sent)]
    [InlineData("pending", CustomerDocumentDeliveryStatus.SentUnconfirmed)]
    [InlineData("failed", CustomerDocumentDeliveryStatus.Failed)]
    public async Task An_uncertain_send_is_settled_from_the_gateways_own_log(string logStatus, CustomerDocumentDeliveryStatus expected)
    {
        await _kit.SetRuntimeAsync();
        var issued = _pacer.Now.AddMinutes(-10);
        var delivery = await UncertainAsync(issued);
        _gateway.Log.Add(new WhatsAppOutboundMessageDto
        {
            Id = "row-1",
            WaMessageId = logStatus == "sent" ? "wamid-from-log" : null,
            ChatId = "263771234567@c.us",
            Body = "Kefalos-Invoice-780100.pdf",
            Type = "document",
            Direction = "outgoing",
            Status = logStatus,
            CreatedAt = issued.AddSeconds(3)
        });

        var result = await PassAsync();

        Assert.Equal(1, result.Reconciled);
        Assert.Empty(_gateway.Documents);
        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(expected, stored.Status);
        if (expected == CustomerDocumentDeliveryStatus.Sent)
            Assert.Equal("wamid-from-log", stored.MessageId);
    }

    [Fact]
    public async Task A_send_the_gateway_never_recorded_is_sent_again()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await UncertainAsync(_pacer.Now.AddMinutes(-10));

        await PassAsync();

        // OpenWA logs a send before it makes it, so no log row means it never got that far.
        Assert.Single(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Sent, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task The_log_is_matched_on_the_file_so_an_earlier_copy_settles_nothing()
    {
        await _kit.SetRuntimeAsync();
        var issued = _pacer.Now.AddMinutes(-10);
        var delivery = await UncertainAsync(issued);
        _gateway.Log.Add(new WhatsAppOutboundMessageDto
        {
            Id = "row-older",
            WaMessageId = "wamid-older",
            ChatId = "263771234567@c.us",
            Body = "Kefalos-Invoice-780100.pdf",
            Type = "document",
            Direction = "outgoing",
            Status = "sent",
            // Sent on purpose the day before: outside this send's window.
            CreatedAt = issued.AddDays(-1)
        });

        await PassAsync();

        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.NotEqual("wamid-older", stored.MessageId);
    }

    // ── What must stop a send ───────────────────────────────────────────

    [Fact]
    public async Task Nothing_is_claimed_while_the_session_is_not_ready()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _gateway.SessionStatus = "disconnected";

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        Assert.Equal(0, _composer.Calls);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
        Assert.Contains(OpenWADispatchOutcomeClassifier.SessionDownAlert, _alerts.Raised);
    }

    [Fact]
    public async Task Nothing_is_claimed_without_a_session_assigned()
    {
        await _kit.SetRuntimeAsync(sessionId: null);
        var delivery = await _kit.AddDeliveryAsync();

        await PassAsync();

        Assert.Equal(0, _composer.Calls);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task A_node_without_the_gateway_configured_claims_nothing()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();

        await PassAsync(openWa: CustomerDocumentTestKit.Gateway(configured: false));

        Assert.Equal(0, _composer.Calls);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task Nothing_starts_once_the_application_is_stopping()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        var lifetime = new FakeLifetime();
        lifetime.StopApplication();

        await PassAsync(lifetime: lifetime);

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task An_opted_out_number_is_withdrawn_without_sending()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync(optedOutAtUtc: DateTime.UtcNow.AddHours(-1));
        var delivery = await _kit.AddDeliveryAsync(row => row.ContactId = contact.Id);

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Cancelled, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task A_one_off_number_opted_out_anywhere_is_withdrawn()
    {
        await _kit.SetRuntimeAsync();
        await _kit.AddContactAsync(cardCode: "SPA070", optedOutAtUtc: DateTime.UtcNow.AddHours(-1));
        var delivery = await _kit.AddDeliveryAsync();

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Cancelled, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task Automatic_sends_wait_for_the_switch_but_a_person_pressing_send_does_not()
    {
        await _kit.SetRuntimeAsync(autoSend: false);
        var automatic = await _kit.AddDeliveryAsync(row => row.Trigger = CustomerDocumentDeliveryTrigger.Auto);
        var manual = await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400101;
            row.SapDocNum = 780101;
            row.DocumentNumber = "780101";
            row.RecipientE164 = "+263772222222";
        });

        await PassAsync();

        Assert.Single(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(automatic.Id)).Status);
        Assert.Equal(CustomerDocumentDeliveryStatus.Sent, (await _kit.ReloadDeliveryAsync(manual.Id)).Status);
    }

    [Fact]
    public async Task Automatic_sends_wait_outside_the_window()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        var automatic = await _kit.AddDeliveryAsync(row => row.Trigger = CustomerDocumentDeliveryTrigger.Auto);
        var catNow = ShopInventory.Services.AuditService.ToCAT(_pacer.Now);
        var closed = catNow.AddHours(2).ToString("HH:mm");
        var closes = catNow.AddHours(3).ToString("HH:mm");

        await PassAsync(CustomerDocumentTestKit.Settings(settings =>
        {
            settings.AutoWindowStartCat = closed;
            settings.AutoWindowEndCat = closes;
        }));

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(automatic.Id)).Status);
    }

    // ── The fiscal receipt ──────────────────────────────────────────────

    [Fact]
    public async Task A_document_without_its_receipt_waits_and_is_looked_at_again_later()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _composer.Answer = _ => DocumentComposition.WaitForFiscal("The invoice has no fiscal receipt yet.");

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.WaitingForFiscal, stored.Status);
        Assert.True(stored.NextAttemptAtUtc > _pacer.Now);
        Assert.Equal("The invoice has no fiscal receipt yet.", stored.StatusReason);
    }

    [Fact]
    public async Task A_document_that_never_gets_its_receipt_is_held_for_a_person()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync(row => row.CreatedAtUtc = DateTime.UtcNow.AddHours(-25));
        _composer.Answer = _ => DocumentComposition.WaitForFiscal("The invoice has no fiscal receipt yet.");

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Held, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
        Assert.Contains(RecordingAlertsCondition.Held, _alerts.Raised);
    }

    [Fact]
    public async Task A_receipt_that_belongs_to_another_document_is_held_not_sent()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _composer.Answer = _ => DocumentComposition.Hold("A fiscal receipt is recorded under invoice number 780100, but it was filed for customer CHE012.");

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Held, stored.Status);
        Assert.Contains("CHE012", stored.StatusReason);
    }

    // ── The number ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_number_without_WhatsApp_is_closed_and_remembered_on_the_contact()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync(whatsAppExists: null);
        var delivery = await _kit.AddDeliveryAsync(row => row.ContactId = contact.Id);
        _gateway.OnCheck = digits => new WhatsAppNumberCheckDto { Number = digits, Exists = false };

        await PassAsync();

        Assert.Equal(["263771234567"], _gateway.NumberChecks);
        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.NotOnWhatsApp, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);

        await using var context = _kit.NewContext();
        var stored = await context.CustomerWhatsAppContacts.AsNoTracking().SingleAsync(row => row.Id == contact.Id);
        Assert.False(stored.WhatsAppExists);
        Assert.NotNull(stored.WhatsAppCheckedAtUtc);
    }

    [Fact]
    public async Task A_number_known_to_be_on_WhatsApp_is_not_asked_about_again()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync(whatsAppExists: true);
        await _kit.AddDeliveryAsync(row => row.ContactId = contact.Id);

        await PassAsync();

        Assert.Empty(_gateway.NumberChecks);
        Assert.Single(_gateway.Documents);
    }

    [Fact]
    public async Task With_the_daily_checks_spent_the_document_is_sent_unchecked()
    {
        await _kit.SetRuntimeAsync();
        var contact = await _kit.AddContactAsync(whatsAppExists: null);
        await _kit.AddDeliveryAsync(row => row.ContactId = contact.Id);

        await PassAsync(CustomerDocumentTestKit.Settings(settings => settings.MaxNumberChecksPerDay = 0));

        Assert.Empty(_gateway.NumberChecks);
        Assert.Single(_gateway.Documents);
    }

    // ── Refusals ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_session_refusal_is_retried_later_and_counts_as_no_send()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _gateway.OnSend = _ => Task.FromException<WhatsAppMessageDispatchDto>(
            FakeOpenWAClient.Refusal(HttpStatusCode.BadRequest, "Session 'documents-session' is not active. Start the session first."));

        await PassAsync();

        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, stored.Status);
        Assert.Equal(1, stored.DispatchAttempts);
        Assert.Null(stored.SendIssuedAtUtc);
        Assert.True(stored.NextAttemptAtUtc > _pacer.Now);
    }

    [Fact]
    public async Task The_last_allowed_attempt_that_does_not_leave_fails_the_document()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync(row => row.DispatchAttempts = 4);
        _gateway.OnSend = _ => Task.FromException<WhatsAppMessageDispatchDto>(
            FakeOpenWAClient.Refusal(HttpStatusCode.ServiceUnavailable, "Engine not ready"));

        await PassAsync();

        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Failed, stored.Status);
        Assert.Equal(5, stored.DispatchAttempts);
    }

    [Fact]
    public async Task A_document_the_gateway_finds_too_large_is_failed_and_reported()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync();
        _gateway.OnSend = _ => Task.FromException<WhatsAppMessageDispatchDto>(
            FakeOpenWAClient.Refusal(HttpStatusCode.RequestEntityTooLarge, "request entity too large"));

        await PassAsync();

        Assert.Equal(CustomerDocumentDeliveryStatus.Failed, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
        Assert.Contains(OpenWADispatchOutcomeClassifier.DocumentTooLargeAlert, _alerts.Raised);
    }

    [Fact]
    public async Task A_refused_key_pauses_the_whole_pass()
    {
        await _kit.SetRuntimeAsync();
        var first = await _kit.AddDeliveryAsync();
        var second = await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400101;
            row.SapDocNum = 780101;
            row.DocumentNumber = "780101";
            row.RecipientE164 = "+263772222222";
            row.CreatedAtUtc = DateTime.UtcNow.AddSeconds(-10);
        });
        _gateway.OnSend = _ => Task.FromException<WhatsAppMessageDispatchDto>(
            FakeOpenWAClient.Refusal(HttpStatusCode.Unauthorized, "Invalid API key"));

        await PassAsync();

        Assert.Single(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(first.Id)).Status);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(second.Id)).Status);
        Assert.Contains(OpenWADispatchOutcomeClassifier.GatewayRefusedAlert, _alerts.Raised);
    }

    // ── Pacing and caps ─────────────────────────────────────────────────

    [Fact]
    public async Task Consecutive_sends_keep_the_gap()
    {
        await _kit.SetRuntimeAsync();
        await _kit.AddDeliveryAsync();
        await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400101;
            row.SapDocNum = 780101;
            row.DocumentNumber = "780101";
            row.RecipientE164 = "+263772222222";
        });

        await PassAsync();

        Assert.Equal(2, _gateway.Documents.Count);
        Assert.Contains(TimeSpan.FromSeconds(6), _pacer.Delays);
    }

    [Fact]
    public async Task The_hourly_cap_holds_across_passes()
    {
        await _kit.SetRuntimeAsync();
        await _kit.AddDeliveryAsync(row =>
        {
            row.Status = CustomerDocumentDeliveryStatus.Sent;
            row.SendIssuedAtUtc = _pacer.Now.AddMinutes(-10);
            row.RecipientE164 = "+263779999999";
        });
        var waiting = await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400101;
            row.SapDocNum = 780101;
            row.DocumentNumber = "780101";
        });

        await PassAsync(CustomerDocumentTestKit.Settings(settings => settings.MaxPerHour = 1));

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, (await _kit.ReloadDeliveryAsync(waiting.Id)).Status);
        Assert.Contains(RecordingAlertsCondition.CapReached, _alerts.Raised);
    }

    [Fact]
    public async Task A_number_at_its_daily_cap_gets_the_rest_tomorrow()
    {
        await _kit.SetRuntimeAsync();
        await _kit.AddDeliveryAsync(row =>
        {
            row.Status = CustomerDocumentDeliveryStatus.Sent;
            row.SendIssuedAtUtc = _pacer.Now.AddMinutes(-1);
        });
        var waiting = await _kit.AddDeliveryAsync(row =>
        {
            row.SapDocEntry = 2400101;
            row.SapDocNum = 780101;
            row.DocumentNumber = "780101";
        });

        await PassAsync(CustomerDocumentTestKit.Settings(settings => settings.MaxPerRecipientPerDay = 1));

        Assert.Empty(_gateway.Documents);
        var stored = await _kit.ReloadDeliveryAsync(waiting.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, stored.Status);
        Assert.Equal(DeliveryBudget.NextCatDayStartUtc(_pacer.Now), stored.NextAttemptAtUtc);
    }

    // ── What an earlier pass left behind ────────────────────────────────

    [Fact]
    public async Task A_claim_that_never_reached_the_send_is_released()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync(row =>
        {
            row.Status = CustomerDocumentDeliveryStatus.Preparing;
            row.ClaimToken = Guid.NewGuid();
            row.ClaimedAtUtc = _pacer.Now.AddMinutes(-20);
        });

        await PassAsync();

        Assert.Single(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Sent, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    [Fact]
    public async Task A_send_cut_off_mid_flight_is_settled_from_the_log_not_sent_twice()
    {
        await _kit.SetRuntimeAsync();
        var issued = _pacer.Now.AddMinutes(-7);
        var delivery = await _kit.AddDeliveryAsync(row =>
        {
            // A pass that died between the gateway call and recording its answer.
            row.Status = CustomerDocumentDeliveryStatus.Sending;
            row.SendIssuedAtUtc = issued;
            row.FileName = "Kefalos-Invoice-780100.pdf";
            row.SessionId = CustomerDocumentTestKit.SessionId;
        });
        _gateway.Log.Add(new WhatsAppOutboundMessageDto
        {
            Id = "row-1",
            WaMessageId = "wamid-delivered",
            ChatId = "263771234567@c.us",
            Body = "Kefalos-Invoice-780100.pdf",
            Type = "document",
            Direction = "outgoing",
            Status = "sent",
            CreatedAt = issued.AddSeconds(2)
        });

        await PassAsync();

        Assert.Empty(_gateway.Documents);
        var stored = await _kit.ReloadDeliveryAsync(delivery.Id);
        Assert.Equal(CustomerDocumentDeliveryStatus.Sent, stored.Status);
        Assert.Equal("wamid-delivered", stored.MessageId);
    }

    [Fact]
    public async Task A_send_cut_off_too_recently_to_settle_stays_uncertain()
    {
        await _kit.SetRuntimeAsync();
        var delivery = await _kit.AddDeliveryAsync(row =>
        {
            row.Status = CustomerDocumentDeliveryStatus.Sending;
            row.SendIssuedAtUtc = _pacer.Now.AddMinutes(-6).AddSeconds(-30);
            row.FileName = "Kefalos-Invoice-780100.pdf";
            row.SessionId = CustomerDocumentTestKit.SessionId;
        });

        await PassAsync(CustomerDocumentTestKit.Settings(settings => settings.UncertainReconcileAfterMinutes = 10));

        Assert.Empty(_gateway.Documents);
        Assert.Equal(CustomerDocumentDeliveryStatus.Uncertain, (await _kit.ReloadDeliveryAsync(delivery.Id)).Status);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<CustomerDocumentDeliveryEntity> UncertainAsync(DateTime issuedUtc) =>
        await _kit.AddDeliveryAsync(row =>
        {
            row.Status = CustomerDocumentDeliveryStatus.Uncertain;
            row.SendIssuedAtUtc = issuedUtc;
            row.FileName = "Kefalos-Invoice-780100.pdf";
            row.SessionId = CustomerDocumentTestKit.SessionId;
            row.CreatedAtUtc = issuedUtc.AddMinutes(-1);
        });

    private async Task<DispatchCustomerDocumentDeliveriesResult> PassAsync(
        CustomerDocumentDeliverySettings? settings = null,
        OpenWASettings? openWa = null,
        FakeLifetime? lifetime = null)
    {
        await using var context = _kit.NewContext();
        var handler = new DispatchCustomerDocumentDeliveriesHandler(
            context,
            _gateway,
            Options.Create(openWa ?? CustomerDocumentTestKit.Gateway()),
            Options.Create(settings ?? CustomerDocumentTestKit.Settings()),
            _composer,
            _alerts,
            _pacer,
            lifetime ?? new FakeLifetime(),
            NullLogger<DispatchCustomerDocumentDeliveriesHandler>.Instance);

        var result = await handler.Handle(new DispatchCustomerDocumentDeliveriesCommand(), CancellationToken.None);
        Assert.False(result.IsError);
        return result.Value;
    }

    private static class RecordingAlertsCondition
    {
        public const string Held = "Held";
        public const string Uncertain = "Uncertain";
        public const string CapReached = "CapReached";
    }
}
