using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments;
using ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerDocumentDeliverySettings;

namespace ShopInventory.Tests;

/// <summary>
/// Which WhatsApp number sends customer documents when nobody has chosen one: the gateway's ready
/// session, without a visit to the settings — and never a guess between several, and never over an
/// administrator who stopped sending.
/// </summary>
public sealed class CustomerDocumentSessionTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();
    private readonly FakeOpenWAClient _gateway = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void The_only_ready_session_is_picked_whatever_it_is_called()
    {
        var pick = CustomerDocumentSession.Pick(
            [Session("a", "fiscal-alerts", "ready"), Session("b", "customer-documents", "qr")],
            "customer-documents");

        Assert.Equal("a", pick.Session?.Id);
    }

    [Fact]
    public void Among_several_ready_sessions_the_one_named_for_documents_is_picked()
    {
        var pick = CustomerDocumentSession.Pick(
            [Session("a", "fiscal-alerts", "ready"), Session("b", "Customer-Documents", "READY")],
            "customer-documents");

        Assert.Equal("b", pick.Session?.Id);
    }

    [Fact]
    public void Several_ready_sessions_with_none_named_for_documents_are_left_to_a_person()
    {
        var pick = CustomerDocumentSession.Pick(
            [Session("a", "fiscal-alerts", "ready"), Session("b", "support", "ready")],
            "customer-documents");

        Assert.Null(pick.Session);
        Assert.Equal(SendingSessionKind.SeveralReady, pick.Kind);
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("qr")]
    [InlineData("")]
    public void A_session_that_is_not_ready_is_never_picked(string status)
    {
        var pick = CustomerDocumentSession.Pick([Session("a", "customer-documents", status)], "customer-documents");

        Assert.Null(pick.Session);
        Assert.Equal(SendingSessionKind.NoneReady, pick.Kind);
    }

    [Fact]
    public async Task A_pick_is_saved_and_recorded_as_automatic()
    {
        var sending = await EnsureAsync();

        Assert.Equal(SendingSessionKind.Chosen, sending.Kind);
        Assert.True(sending.PickedNow);
        Assert.Equal(CustomerDocumentTestKit.SessionId, sending.SessionId);

        await using var context = _kit.NewContext();
        var saved = await CustomerDocumentDeliveryKeys.ReadAsync(context, CancellationToken.None);
        Assert.Equal(CustomerDocumentTestKit.SessionId, saved.WhatsAppSessionId);
        Assert.Equal(CustomerDocumentSession.AutomaticActor, saved.ChangedBy);
        Assert.False(saved.SendingStopped);
    }

    [Fact]
    public async Task A_session_an_administrator_chose_is_used_without_asking_the_gateway()
    {
        await _kit.SetRuntimeAsync(sessionId: "their-choice");
        _gateway.SessionsFailure = new InvalidOperationException("The gateway must not be asked.");

        var sending = await EnsureAsync();

        Assert.Equal("their-choice", sending.SessionId);
        Assert.False(sending.PickedNow);
    }

    [Fact]
    public async Task Sending_an_administrator_stopped_is_not_started_again_by_a_pick()
    {
        await _kit.SetRuntimeAsync(sessionId: null, stopped: true);

        var sending = await EnsureAsync();

        Assert.Equal(SendingSessionKind.Stopped, sending.Kind);
        Assert.Null(await _kit.SavedSessionIdAsync());
    }

    [Fact]
    public async Task A_gateway_that_does_not_answer_is_not_an_answer()
    {
        _gateway.SessionsFailure = new HttpRequestException("connection refused");

        var sending = await EnsureAsync();

        Assert.Equal(SendingSessionKind.CannotAsk, sending.Kind);
        Assert.Null(sending.Refusal("customer-documents"));
    }

    [Fact]
    public async Task With_nothing_saved_automatic_sending_is_on_and_a_saved_off_stays_off()
    {
        await using (var context = _kit.NewContext())
        {
            Assert.True((await CustomerDocumentDeliveryKeys.ReadAsync(context, CancellationToken.None)).AutoSendEnabled);
        }

        await _kit.SetRuntimeAsync(autoSend: false);

        await using (var context = _kit.NewContext())
        {
            Assert.False((await CustomerDocumentDeliveryKeys.ReadAsync(context, CancellationToken.None)).AutoSendEnabled);
        }
    }

    // ── What an administrator saves ─────────────────────────────────────

    [Fact]
    public async Task Saving_None_stops_sending_until_a_number_is_chosen()
    {
        var saved = await SaveAsync(new UpdateCustomerDocumentDeliverySettingsRequest { StopSending = true, MaxAutoPerDay = 20 });

        Assert.True(saved.SendingStopped);
        Assert.Null(saved.SessionId);
        Assert.Equal(SendingSessionKind.Stopped, (await EnsureAsync()).Kind);

        var restarted = await SaveAsync(new UpdateCustomerDocumentDeliverySettingsRequest
        {
            WhatsAppSessionId = CustomerDocumentTestKit.SessionId,
            MaxAutoPerDay = 20
        });

        Assert.False(restarted.SendingStopped);
        Assert.Equal(CustomerDocumentTestKit.SessionId, (await EnsureAsync()).SessionId);
    }

    [Fact]
    public async Task Saving_with_no_session_and_no_stop_hands_the_choice_back_to_the_system()
    {
        await SaveAsync(new UpdateCustomerDocumentDeliverySettingsRequest { WhatsAppSessionId = "their-choice", MaxAutoPerDay = 20 }, knownSession: "their-choice");

        var saved = await SaveAsync(new UpdateCustomerDocumentDeliverySettingsRequest { AutoSendEnabled = true, MaxAutoPerDay = 20 });

        Assert.False(saved.SendingStopped);
        Assert.Null(saved.SessionId);
        var sending = await EnsureAsync();
        Assert.True(sending.PickedNow);
        Assert.Equal(CustomerDocumentTestKit.SessionId, sending.SessionId);
    }

    [Fact]
    public async Task The_status_says_what_the_next_pass_will_take_without_taking_it()
    {
        _gateway.Sessions =
        [
            Session("a", "fiscal-alerts", "ready"),
            Session(CustomerDocumentTestKit.SessionId, "customer-documents", "ready")
        ];

        var status = await StatusAsync();

        Assert.Null(status.SessionId);
        Assert.Equal("customer-documents", status.AutomaticSessionName);
        Assert.False(status.SeveralSessionsReady);
        Assert.Null(await _kit.SavedSessionIdAsync());
    }

    [Fact]
    public async Task The_status_says_when_a_person_has_to_choose_between_several()
    {
        _gateway.Sessions = [Session("a", "fiscal-alerts", "ready"), Session("b", "support", "ready")];

        var status = await StatusAsync();

        Assert.Null(status.AutomaticSessionName);
        Assert.True(status.SeveralSessionsReady);
    }

    [Fact]
    public async Task A_stop_sent_with_a_session_is_not_a_stop()
    {
        var saved = await SaveAsync(new UpdateCustomerDocumentDeliverySettingsRequest
        {
            WhatsAppSessionId = CustomerDocumentTestKit.SessionId,
            StopSending = true,
            MaxAutoPerDay = 20
        });

        Assert.False(saved.SendingStopped);
        Assert.Equal(CustomerDocumentTestKit.SessionId, saved.SessionId);
    }

    private async Task<CustomerDocumentDeliveryStatusDto> SaveAsync(
        UpdateCustomerDocumentDeliverySettingsRequest request,
        string? knownSession = null)
    {
        if (knownSession is not null)
        {
            _gateway.Sessions = [Session(knownSession, "another-line", "ready")];
        }

        try
        {
            await using var context = _kit.NewContext();
            var handler = new UpdateCustomerDocumentDeliverySettingsHandler(
                context,
                _gateway,
                Options.Create(CustomerDocumentTestKit.Gateway()),
                Options.Create(CustomerDocumentTestKit.Settings()),
                new RecordingAuditService(),
                NullLogger<UpdateCustomerDocumentDeliverySettingsHandler>.Instance);

            var result = await handler.Handle(
                new UpdateCustomerDocumentDeliverySettingsCommand(request, CustomerDocumentTestKit.Cashier),
                CancellationToken.None);

            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
            return result.Value;
        }
        finally
        {
            _gateway.Sessions = null;
        }
    }

    private async Task<CustomerDocumentDeliveryStatusDto> StatusAsync()
    {
        await using var context = _kit.NewContext();
        return await CustomerDocumentStatusReader.ReadAsync(
            context,
            _gateway,
            CustomerDocumentTestKit.Gateway(),
            CustomerDocumentTestKit.Settings(),
            NullLogger.Instance,
            CancellationToken.None);
    }

    private async Task<SendingSession> EnsureAsync()
    {
        await using var context = _kit.NewContext();
        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, CancellationToken.None);
        return await CustomerDocumentSession.EnsureAsync(
            context,
            _gateway,
            CustomerDocumentTestKit.Gateway(),
            CustomerDocumentTestKit.Settings(),
            runtime,
            NullLogger.Instance,
            CancellationToken.None);
    }

    private static WhatsAppSessionDto Session(string id, string name, string status) =>
        new() { Id = id, Name = name, Status = status };
}
