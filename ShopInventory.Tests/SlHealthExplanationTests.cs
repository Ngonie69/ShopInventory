using ShopInventory.Services.SlHealth;

namespace ShopInventory.Tests;

/// <summary>
/// The words SL Health's answer becomes: a few for the staff banner, built from structure alone, and
/// a fuller line for the outage row and the health check.
/// </summary>
public sealed class SlHealthExplanationTests
{
    // 00:00 UTC is 02:00 in CAT.
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_planned_window_says_until_when_in_cat_and_what_for()
    {
        var status = Status(maintenanceReason: "HANA backup", maintenanceUntil: Now.AddHours(1));

        Assert.Equal("planned maintenance until 03:00 (HANA backup)", SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_window_ending_on_a_later_day_says_which_day()
    {
        var status = Status(maintenanceReason: "Upgrade to FP 2508", maintenanceUntil: Now.AddDays(2).AddHours(1));

        Assert.Equal(
            "planned maintenance until Sat 3 Jan 03:00 (Upgrade to FP 2508)",
            SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_window_that_has_already_ended_is_not_mentioned()
    {
        var status = Status(maintenanceReason: "HANA backup", maintenanceUntil: Now.AddMinutes(-1));

        Assert.Null(SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_service_layer_restart_is_sap_being_restarted()
    {
        var status = Status(level: "Down", advice: "Unavailable", restartTarget: "ServiceLayer");

        Assert.Equal("SAP is being restarted", SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_server_tools_restart_is_named_as_that()
    {
        var status = Status(restartTarget: "ServerTools");

        Assert.Equal("SAP's Server Tools are being restarted", SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_restart_inside_a_planned_window_says_both()
    {
        var status = Status(restartTarget: "ServiceLayer", maintenanceReason: "HANA backup", maintenanceUntil: Now.AddHours(1));

        Assert.Equal(
            "SAP is being restarted; planned maintenance until 03:00 (HANA backup)",
            SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_failing_login_with_server_tools_down_names_server_tools()
    {
        var status = Status(level: "Critical", advice: "HoldBackground", loginOk: false, serverToolsOk: false);

        Assert.Equal("login is failing: Server Tools not answering", SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void Server_tools_down_while_logins_still_work_says_new_logins_will_fail()
    {
        var status = Status(level: "Critical", advice: "HoldBackground", serverToolsOk: false);

        Assert.Equal("new logins will fail: Server Tools not answering", SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_failing_login_alone_and_a_failing_query_are_each_named()
    {
        Assert.Equal(
            "login is failing",
            SlHealthExplanation.ForStaff(Status(level: "Critical", advice: "HoldBackground", loginOk: false), Now.UtcDateTime));
        Assert.Equal(
            "the Items query is failing",
            SlHealthExplanation.ForStaff(Status(level: "Critical", advice: "HoldBackground", queryOk: false), Now.UtcDateTime));
        Assert.Equal(
            "login is failing; the Items query is failing",
            SlHealthExplanation.ForStaff(Status(level: "Critical", advice: "HoldBackground", loginOk: false, queryOk: false), Now.UtcDateTime));
    }

    [Fact]
    public void With_nothing_wrong_there_is_nothing_to_say()
    {
        Assert.Null(SlHealthExplanation.ForStaff(Status(), Now.UtcDateTime));
        Assert.Null(SlHealthExplanation.ForRecord(Status(), Now.UtcDateTime));
        Assert.Null(SlHealthExplanation.ForStaff(null, Now.UtcDateTime));
        Assert.Null(SlHealthExplanation.ForRecord(null, Now.UtcDateTime));
    }

    [Fact]
    public void Staff_are_never_shown_sl_healths_own_reasons_or_a_checks_error_text()
    {
        // Both are written for administrators and can name a server.
        var status = Status(
            level: "Down", advice: "Unavailable", pingOk: false, loginOk: false, queryOk: false,
            reasons: ["Service Layer is not answering /ping", "HANA not reachable on kfldb:30015: Connection refused"]);

        var staff = SlHealthExplanation.ForStaff(status, Now.UtcDateTime);

        Assert.Equal("login is failing; the Items query is failing", staff);
        Assert.DoesNotContain("kfldb", staff);
        Assert.DoesNotContain("timed out after 30 s", staff);
    }

    [Fact]
    public void The_record_falls_back_to_the_level_and_sl_healths_reasons()
    {
        var status = Status(
            level: "Down", advice: "Unavailable", pingOk: false,
            reasons: ["Service Layer is not answering /ping", "HANA not reachable on kfldb:30015: Connection refused"]);

        Assert.Null(SlHealthExplanation.ForStaff(status, Now.UtcDateTime));
        Assert.Equal(
            "DOWN: Service Layer is not answering /ping; HANA not reachable on kfldb:30015: Connection refused",
            SlHealthExplanation.ForRecord(status, Now.UtcDateTime));
    }

    [Fact]
    public void A_reason_somebody_typed_is_kept_to_one_short_line()
    {
        var typed = "HANA\r\nbackup\t" + new string('x', 400);
        var status = Status(maintenanceReason: typed, maintenanceUntil: Now.AddHours(1));

        var staff = SlHealthExplanation.ForStaff(status, Now.UtcDateTime)!;

        Assert.StartsWith("planned maintenance until 03:00 (HANA backup xxx", staff);
        Assert.DoesNotContain('\n', staff);
        Assert.InRange(staff.Length, 1, 160);

        var long_ = Status(level: "Down", advice: "Unavailable", reasons: [new string('y', 2000)]);
        Assert.Equal(SlHealthExplanation.MaxRecordLength, SlHealthExplanation.ForRecord(long_, Now.UtcDateTime)!.Length);
    }

    private static SlHealthStatus Status(
        string level = "Healthy",
        string advice = "Proceed",
        bool pingOk = true,
        bool loginOk = true,
        bool queryOk = true,
        bool serverToolsOk = true,
        string? maintenanceReason = null,
        DateTimeOffset? maintenanceUntil = null,
        string? restartTarget = null,
        string[]? reasons = null) =>
        SlHealthJson.Parse(SlHealthJson.Status(
            level, advice, false, pingOk, loginOk, queryOk, serverToolsOk, maintenanceReason, maintenanceUntil, restartTarget, reasons, now: Now));
}
