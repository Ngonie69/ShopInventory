using ShopInventory.Web.Components;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// What the "Send documents from" picker on /whatsapp-deliveries says about a saved session the
/// gateway's list does not hold. Gone and unknown are different claims: the first only when the
/// gateway answered, the second when it did not — the 16 Oct 2026 outage showed "not on the gateway"
/// in red for a session that was fine, because an unreachable gateway comes back as an empty list.
/// </summary>
public sealed class WhatsAppSessionPickerTests
{
    private const string SavedId = "0f9a3c2e-5b71-4d0a-9e44-2a7d1c8b6f10";

    [Fact]
    public void A_saved_session_the_gateway_answered_without_is_not_on_the_gateway()
    {
        var saved = SavedOption(new CustomerDocumentDeliveryStatusModel
        {
            GatewayConfigured = true,
            SessionId = SavedId,
            SessionsListed = true,
            Sessions = [new WhatsAppSessionOptionModel { Id = "other", Name = "fiscal-alerts", Status = "ready" }]
        });

        Assert.Equal("Saved — not on the gateway", saved.Label);
        Assert.Equal("bad", saved.Family);
        Assert.Equal("0f9a3c2e", saved.Hint);
    }

    [Fact]
    public void A_saved_session_is_not_called_gone_when_the_gateway_did_not_answer()
    {
        var saved = SavedOption(new CustomerDocumentDeliveryStatusModel
        {
            GatewayConfigured = true,
            SessionId = SavedId,
            SessionsListed = false,
            SessionError = "The WhatsApp gateway could not be reached."
        });

        Assert.Equal("Saved — gateway not answering", saved.Label);
        Assert.Equal("warn", saved.Family);
        Assert.Equal("0f9a3c2e", saved.Hint);
    }

    [Fact]
    public void A_saved_session_the_gateway_lists_appears_once_under_its_own_name()
    {
        var options = WhatsAppDeliveries.BuildSessionOptions(new CustomerDocumentDeliveryStatusModel
        {
            GatewayConfigured = true,
            SessionId = SavedId,
            SessionsListed = true,
            Sessions = [new WhatsAppSessionOptionModel { Id = SavedId, Name = "customer-documents", Status = "ready", Phone = "263717000002" }]
        });

        var saved = Assert.Single(options, option => option.Value == SavedId);
        Assert.Equal("customer-documents", saved.Label);
        Assert.Equal("good", saved.Family);
    }

    private static NocturneSelectOption<string> SavedOption(CustomerDocumentDeliveryStatusModel status) =>
        Assert.Single(WhatsAppDeliveries.BuildSessionOptions(status), option => option.Value == SavedId);
}
