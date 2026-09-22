using ShopInventory.Features.Maintenance;

namespace ShopInventory.Tests;

/// <summary>
/// The rule the maintenance lockout comes down to: may this one request through?
///
/// The feature is a switch an operator throws before a migration so that nothing posts into a
/// database that is being restored. Three ways it could fail are worth more than the rest: letting
/// a transaction through while it is on, taking away more than it should — a driver who cannot look
/// up a price for the next four hours is a field outage, not maintenance — and reaching an audience
/// nobody ticked. Most of what follows is one of the three.
/// </summary>
public sealed class MaintenanceGateTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);

    private static MaintenanceState On(
        MaintenanceScope scope = MaintenanceScope.Transactions,
        IReadOnlyList<string>? apps = null,
        IReadOnlyList<MaintenanceAudience>? audiences = null,
        DateTime? endsAtUtc = null) =>
        new(
            Enabled: true,
            Scope: scope,
            Audiences: audiences ?? [MaintenanceAudience.MobileApps],
            Message: "Down for the stock migration until 20:00.",
            AppIds: apps ?? [],
            StartedAtUtc: Now.AddMinutes(-5),
            EndsAtUtc: endsAtUtc,
            UpdatedBy: "ngoni");

    private static MaintenanceDecision Evaluate(
        MaintenanceState state,
        string method,
        string path,
        MaintenanceAudience audience = MaintenanceAudience.MobileApps,
        string? policyKey = "kefalos-vansales",
        bool isAdmin = false,
        DateTime? nowUtc = null) =>
        MaintenanceGate.Evaluate(
            state,
            nowUtc ?? Now,
            new MaintenanceCaller(audience, policyKey),
            isAdmin,
            method,
            path);

    [Fact]
    public void With_the_switch_off_a_phone_transacts_as_usual()
    {
        var decision = Evaluate(MaintenanceState.Off, "POST", "/api/vansales/sales");

        Assert.False(decision.IsBlocked);
    }

    [Fact]
    public void A_phone_cannot_post_a_sale_while_the_lockout_is_on()
    {
        var decision = Evaluate(On(), "POST", "/api/vansales/sales");

        Assert.True(decision.IsBlocked);
        Assert.Equal("Down for the stock migration until 20:00.", decision.Message);
    }

    [Theory]
    [InlineData("POST", "/api/vansales/order")]
    [InlineData("POST", "/api/vansales/inventory/confirm")]
    [InlineData("POST", "/api/vansales/stock/position")]
    [InlineData("POST", "/api/vansales/breakages")]
    [InlineData("POST", "/api/vansales/fiscal/day-close")]
    [InlineData("POST", "/api/vansales/attendance")]
    [InlineData("PUT", "/api/vansales/customer/C001")]
    [InlineData("DELETE", "/api/vansales/customer/C001")]
    [InlineData("PATCH", "/api/salesorder/17")]
    public void Everything_that_changes_something_is_refused(string method, string path)
    {
        Assert.True(Evaluate(On(), method, path).IsBlocked);
    }

    [Theory]
    [InlineData("/api/vansales/customer")]
    [InlineData("/api/vansales/stock/position")]
    [InlineData("/api/vansales/day/current")]
    [InlineData("/api/product")]
    public void Reads_keep_working_under_the_transaction_lockout(string path)
    {
        // The point of the default scope. A rep mid-round can still price an order and look up
        // what they already captured; they simply cannot send it.
        Assert.False(Evaluate(On(), "GET", path).IsBlocked);
    }

    [Theory]
    [InlineData("/api/vansales/sales-order/history")]
    [InlineData("/api/vansales/order/history")]
    [InlineData("/api/stock/warehouse/VAN01/sales")]
    [InlineData("/api/crates/pods/validate-bulk")]
    [InlineData("/api/invoice/pods/validate-bulk")]
    public void Searches_that_are_POSTs_only_because_their_filter_is_a_body_still_work(string path)
    {
        // Verb alone would take these away, and they change nothing. This is the whole reason the
        // gate keeps an allowlist rather than trusting the method.
        Assert.False(Evaluate(On(), "POST", path).IsBlocked);
    }

    [Fact]
    public void An_unlisted_POST_is_treated_as_a_transaction()
    {
        // The safe default: a route added next year is refused until somebody decides it is a read.
        Assert.True(Evaluate(On(), "POST", "/api/vansales/something-new").IsBlocked);
    }

    [Theory]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/auth/refresh")]
    [InlineData("POST", "/api/vansales/auth/login")]
    [InlineData("POST", "/api/van-sales-customer/auth/otp/verify")]
    [InlineData("POST", "/api/pushnotification/register")]
    [InlineData("GET", "/api/appversion/mobile")]
    [InlineData("GET", "/api/maintenance/mobile/status")]
    [InlineData("GET", "/api/health/ready")]
    public void Signing_in_and_finding_out_about_the_lockout_stay_open(string method, string path)
    {
        // Including under the strictest scope. An app that cannot sign in shows the user a failed
        // login, and they read that as their password being wrong rather than as maintenance.
        Assert.False(Evaluate(On(MaintenanceScope.All), method, path).IsBlocked);
    }

    [Fact]
    public void The_all_scope_refuses_reads_too()
    {
        Assert.True(Evaluate(On(MaintenanceScope.All), "GET", "/api/product").IsBlocked);
    }

    [Fact]
    public void A_lockout_on_the_phones_leaves_the_portal_and_the_tills_trading()
    {
        // The audiences are the whole point: freezing the vans for a stock take must not stop the
        // office invoicing, and before audiences existed this was the only behaviour there was.
        var state = On(MaintenanceScope.All);

        Assert.False(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal).IsBlocked);
        Assert.False(Evaluate(state, "POST", "/api/desktopintegration/invoices", MaintenanceAudience.OtherClients).IsBlocked);
    }

    [Fact]
    public void A_lockout_on_the_portal_refuses_the_portal_and_leaves_the_phones_trading()
    {
        var state = On(audiences: [MaintenanceAudience.WebPortal]);

        Assert.True(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal).IsBlocked);
        Assert.False(Evaluate(state, "POST", "/api/vansales/sales").IsBlocked);
    }

    [Fact]
    public void Ticking_every_audience_stops_everything()
    {
        // What "freeze all transactions" has to mean. Every request the API serves is one of these
        // three, so if any of them got through here the screen would be lying.
        var state = On(audiences: MaintenanceAudiences.All);

        Assert.True(Evaluate(state, "POST", "/api/vansales/sales", MaintenanceAudience.MobileApps).IsBlocked);
        Assert.True(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal).IsBlocked);
        Assert.True(Evaluate(state, "POST", "/api/desktopintegration/invoices", MaintenanceAudience.OtherClients).IsBlocked);
    }

    [Fact]
    public void An_admin_keeps_transacting_while_the_portal_is_frozen()
    {
        // Somebody has to be able to fix the row that caused the maintenance, and it is the same
        // person who threw the switch. Without this, freezing the portal freezes its own off switch
        // in everything but name.
        var state = On(MaintenanceScope.All, audiences: [MaintenanceAudience.WebPortal]);

        Assert.False(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal, isAdmin: true).IsBlocked);
        Assert.True(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal, isAdmin: false).IsBlocked);
    }

    [Fact]
    public void The_admin_exemption_does_not_reach_the_phones_or_the_tills()
    {
        // A handset held by an admin is still a handset in a van, and the lockout is about the van.
        // An exemption that followed the person rather than the audience would put the one account
        // most likely to be signed in everywhere straight through a blanket freeze.
        var state = On(audiences: MaintenanceAudiences.All);

        Assert.True(Evaluate(state, "POST", "/api/vansales/sales", MaintenanceAudience.MobileApps, isAdmin: true).IsBlocked);
        Assert.True(Evaluate(state, "POST", "/api/desktopintegration/invoices", MaintenanceAudience.OtherClients, isAdmin: true).IsBlocked);
    }

    [Fact]
    public void Naming_apps_narrows_the_phones_and_nothing_else()
    {
        // The app list is a mobile-only filter. Read as a filter on everything, a lockout on van
        // sales and the portal would leave the portal trading — which is the sort of thing nobody
        // notices until the invoices are already in.
        var state = On(apps: ["kefalos-vansales"], audiences: [MaintenanceAudience.MobileApps, MaintenanceAudience.WebPortal]);

        Assert.True(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal, policyKey: null).IsBlocked);
        Assert.False(Evaluate(state, "POST", "/api/crates/pods", policyKey: "cheeseman-driver").IsBlocked);
    }

    [Fact]
    public void The_switch_that_lifts_the_lockout_is_never_frozen()
    {
        // The recovery path. Frozen under an All lockout on the portal, this feature would have to
        // be turned off with a SQL statement against the database somebody was restoring.
        var state = On(MaintenanceScope.All, audiences: MaintenanceAudiences.All);

        Assert.False(Evaluate(state, "GET", "/api/maintenance", MaintenanceAudience.WebPortal).IsBlocked);
        Assert.False(Evaluate(state, "PUT", "/api/maintenance", MaintenanceAudience.WebPortal).IsBlocked);
        Assert.False(Evaluate(state, "GET", "/api/maintenance/status", MaintenanceAudience.OtherClients).IsBlocked);
    }

    [Fact]
    public void An_audience_nobody_ticked_falls_back_to_the_phones_rather_than_to_everybody()
    {
        // Only reachable from a hand-edited SystemConfigs row. Widening it to everything would turn
        // somebody's van lockout into a company-wide one on the next restart.
        var state = On(audiences: []);

        Assert.True(Evaluate(state, "POST", "/api/vansales/sales").IsBlocked);
        Assert.False(Evaluate(state, "POST", "/api/invoice", MaintenanceAudience.WebPortal).IsBlocked);
    }

    [Fact]
    public void A_lockout_aimed_at_one_app_leaves_the_others_trading()
    {
        var state = On(apps: ["kefalos-vansales"]);

        Assert.True(Evaluate(state, "POST", "/api/vansales/sales", policyKey: "kefalos-vansales").IsBlocked);
        Assert.False(Evaluate(state, "POST", "/api/crates/pods", policyKey: "cheeseman-driver").IsBlocked);
    }

    [Fact]
    public void A_narrowed_lockout_does_not_catch_an_app_that_did_not_name_itself()
    {
        // An old build sends no X-App-Id. Refusing it here would take the POD app down because of
        // maintenance aimed at van sales.
        var state = On(apps: ["kefalos-vansales"]);

        Assert.False(Evaluate(state, "POST", "/api/crates/pods", policyKey: null).IsBlocked);
    }

    [Fact]
    public void A_blanket_lockout_catches_an_app_that_did_not_name_itself()
    {
        // The other half, and the more important one: "stop the phones" must not be escapable by a
        // build too old to say which phone it is.
        Assert.True(Evaluate(On(), "POST", "/api/vansales/sales", policyKey: null).IsBlocked);
    }

    [Fact]
    public void A_window_that_has_run_out_stops_applying_without_anybody_flipping_the_switch()
    {
        // The failure this feature invites: maintenance ends at 02:00 and the vans are still locked
        // out at 08:00 because whoever turned it on went to bed.
        var state = On(endsAtUtc: Now.AddMinutes(-1));

        Assert.False(Evaluate(state, "POST", "/api/vansales/sales").IsBlocked);
    }

    [Fact]
    public void A_window_still_running_applies()
    {
        var state = On(endsAtUtc: Now.AddHours(2));
        var decision = Evaluate(state, "POST", "/api/vansales/sales");

        Assert.True(decision.IsBlocked);
        Assert.Equal(TimeSpan.FromHours(2), decision.RetryAfter);
    }

    [Fact]
    public void Without_a_window_the_phone_is_told_to_come_back_in_five_minutes()
    {
        // Long enough that a van full of handsets is not hammering an API mid-maintenance, short
        // enough that trading resumes promptly once the switch goes back.
        Assert.Equal(MaintenanceGate.DefaultRetryAfter, Evaluate(On(), "POST", "/api/vansales/sales").RetryAfter);
    }

    [Fact]
    public void The_built_in_wording_is_used_when_the_operator_wrote_none()
    {
        var state = On() with { Message = "   " };

        Assert.Equal(MaintenanceState.DefaultMessage, Evaluate(state, "POST", "/api/vansales/sales").Message);
    }

    [Theory]
    [InlineData("/API/VanSales/Sales")]
    [InlineData("/api/vansales/sales/")]
    [InlineData("/api/vansales/sales?draft=true")]
    public void Casing_trailing_slashes_and_query_strings_do_not_get_past_it(string path)
    {
        Assert.True(Evaluate(On(), "POST", path).IsBlocked);
    }

    [Fact]
    public void An_exempt_prefix_does_not_match_a_route_that_merely_starts_with_it()
    {
        // "/api/auth" must not open "/api/authorisations".
        Assert.True(Evaluate(On(), "POST", "/api/authorisations").IsBlocked);
    }
}
