using ShopInventory.Features.Maintenance;

namespace ShopInventory.Tests;

/// <summary>
/// The rule the maintenance lockout comes down to: may this one request through?
///
/// The feature is a switch an operator throws before a migration so that no handset in a van posts
/// an invoice into a database that is being restored. Two ways it could fail are worth more than
/// the rest: letting a transaction through while it is on, and taking away more than it should —
/// a driver who cannot look up a price for the next four hours is a field outage, not maintenance.
/// Most of what follows is one or the other.
/// </summary>
public sealed class MaintenanceGateTests
{
    private static readonly DateTime Now = new(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);

    private static MaintenanceState On(
        MaintenanceScope scope = MaintenanceScope.Transactions,
        IReadOnlyList<string>? apps = null,
        DateTime? endsAtUtc = null) =>
        new(
            Enabled: true,
            Scope: scope,
            Message: "Down for the stock migration until 20:00.",
            AppIds: apps ?? [],
            StartedAtUtc: Now.AddMinutes(-5),
            EndsAtUtc: endsAtUtc,
            UpdatedBy: "ngoni");

    private static MaintenanceDecision Evaluate(
        MaintenanceState state,
        string method,
        string path,
        bool isMobileApp = true,
        string? policyKey = "kefalos-vansales",
        DateTime? nowUtc = null) =>
        MaintenanceGate.Evaluate(state, nowUtc ?? Now, isMobileApp, policyKey, method, path);

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
    public void The_web_and_the_desktop_till_are_never_locked_out()
    {
        // This switch is for handsets in vans. The people at desks can be told.
        var decision = Evaluate(On(MaintenanceScope.All), "POST", "/api/vansales/sales", isMobileApp: false);

        Assert.False(decision.IsBlocked);
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
