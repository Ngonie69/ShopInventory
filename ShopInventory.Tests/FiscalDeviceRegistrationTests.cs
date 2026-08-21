using ShopInventory.Common.Fiscalization;

namespace ShopInventory.Tests;

/// <summary>
/// The rules that decide whether a ZIMRA fiscal device may be handed to a van handset.
///
/// Every one of these refusals is silent on the handset. A van given the wrong device does not fail at
/// the moment of registration; it fails days later, out of coverage, having already printed receipts into
/// customers' hands — and the only remedy at that point is a manual credit note against a fiscal
/// document. So the asserted severity matters as much as the finding: a Warn that should have been a
/// Block is a forked receipt chain, and a Block that should have been a Warn is a van that cannot trade
/// in a dead spot for no reason.
/// </summary>
public sealed class FiscalDeviceRegistrationTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private const int VanDevice = 7;
    private const int ShopDevice = 1;

    /// <summary>A van handset on a role that may carry a device, holding nothing yet.</summary>
    private static FiscalDeviceRegistrationTarget Van(
        bool active = true,
        bool roleSupports = true,
        bool alreadyHolds = false) =>
        new("VAN003 (Test Sales)", active, roleSupports, alreadyHolds);

    /// <summary>A device the platform describes the way a van's device should look.</summary>
    private static FiscalDeviceRegistrationInput Input(
        int deviceId = VanDevice,
        int pinned = ShopDevice,
        bool reachable = true,
        string? platformError = null,
        string? mode = "Offline",
        DateTime? certificateValidTill = null,
        string? currentHolder = null,
        FiscalDeviceRegistrationTarget? target = null) =>
        new(
            DeviceId: deviceId,
            PinnedDefaultDeviceId: pinned,
            PlatformReachable: reachable,
            PlatformError: platformError,
            DeviceSerialNo: "0000000007",
            OperatingMode: mode,
            CertificateValidTill: certificateValidTill ?? Now.AddYears(1),
            CurrentHolderLabel: currentHolder,
            Target: target ?? Van(),
            NowUtc: Now);

    private static FiscalDeviceRegistrationFinding? Find(FiscalDeviceRegistrationInput input, string code) =>
        FiscalDeviceRegistration.Inspect(input).FirstOrDefault(finding => finding.Code == code);

    [Fact]
    public void A_free_offline_device_on_a_pinned_server_is_allowed()
    {
        var findings = FiscalDeviceRegistration.Inspect(Input());

        Assert.False(FiscalDeviceRegistration.IsBlocked(findings));
        Assert.Empty(findings);
    }

    /// <summary>
    /// The mistake this whole screen exists to stop. An Online device is one whose sequence FDMS owns, so
    /// a handset signing its own receipts into it forks the chain.
    /// </summary>
    [Theory]
    [InlineData("Online")]
    [InlineData("online")]
    [InlineData("ONLINE")]
    public void An_online_device_is_refused_however_it_is_spelled(string mode)
    {
        var finding = Find(Input(mode: mode), "WrongOperatingMode");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }

    /// <summary>The platform's own spelling and casing is its business, not this app's.</summary>
    [Theory]
    [InlineData("Offline")]
    [InlineData("offline")]
    [InlineData("  Offline  ")]
    public void An_offline_device_is_accepted_however_it_is_spelled(string mode)
    {
        Assert.Null(Find(Input(mode: mode), "WrongOperatingMode"));
    }

    /// <summary>
    /// A mode we cannot read is refused rather than assumed. Guessing Offline here would be guessing in
    /// the direction that forks a chain.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_device_with_no_stated_mode_is_refused(string? mode)
    {
        var finding = Find(Input(mode: mode), "OperatingModeUnknown");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }

    [Fact]
    public void The_servers_own_pinned_device_cannot_be_given_to_a_van()
    {
        var finding = Find(Input(deviceId: ShopDevice, pinned: ShopDevice), "DeviceIsTheServersOwn");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }

    /// <summary>
    /// Unpinned is a warning, not a refusal. The server then submits with device id 0, which the platform
    /// reads as "walk every configured device until one accepts" — including this one — but the pin is a
    /// deployment setting the office may not be able to change from this screen, so it is said rather
    /// than enforced.
    /// </summary>
    [Fact]
    public void An_unpinned_server_warns_without_refusing()
    {
        var findings = FiscalDeviceRegistration.Inspect(Input(pinned: 0));

        var finding = findings.Single(f => f.Code == "DefaultDeviceUnpinned");

        Assert.Equal(FiscalDeviceRegistrationSeverity.Warn, finding.Severity);
        Assert.False(FiscalDeviceRegistration.IsBlocked(findings));
    }

    /// <summary>
    /// An unpinned server does not turn every device into the server's own. The two findings are about
    /// different things and must not collapse into one.
    /// </summary>
    [Fact]
    public void An_unpinned_server_does_not_claim_the_device_as_its_own()
    {
        Assert.Null(Find(Input(pinned: 0), "DeviceIsTheServersOwn"));
    }

    [Fact]
    public void A_device_another_handset_carries_is_refused_once_a_van_is_named()
    {
        var finding = Find(Input(currentHolder: "VAN002 (Other Rep)"), "HeldByAnotherHandset");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
        Assert.Contains("VAN002 (Other Rep)", finding.Message);
    }

    /// <summary>
    /// Before a van is named nothing is being asked for yet, so who holds the device is a fact to report
    /// rather than a refusal. Blocking here would paint the screen red at someone who has only typed a
    /// number.
    /// </summary>
    [Fact]
    public void A_device_another_handset_carries_only_warns_while_no_van_is_named()
    {
        // `with` rather than the helper's target argument: the helper defaults a van in, and "no van has
        // been named yet" is exactly what this test is about.
        var findings = FiscalDeviceRegistration.Inspect(
            Input(currentHolder: "VAN002 (Other Rep)") with { Target = null });

        var finding = findings.Single(f => f.Code == "HeldByAnotherHandset");

        Assert.Equal(FiscalDeviceRegistrationSeverity.Warn, finding.Severity);
        Assert.False(FiscalDeviceRegistration.IsBlocked(findings));
    }

    /// <summary>Re-saving the handset that already holds the device is a no-op, not a conflict with itself.</summary>
    [Fact]
    public void The_handset_that_already_holds_the_device_is_not_blocked_by_itself()
    {
        var findings = FiscalDeviceRegistration.Inspect(Input(
            currentHolder: "VAN003 (Test Sales)",
            target: Van(alreadyHolds: true)));

        Assert.False(FiscalDeviceRegistration.IsBlocked(findings));
        Assert.Equal("AlreadyRegistered", findings.Single().Code);
    }

    [Fact]
    public void An_inactive_handset_cannot_be_given_a_device()
    {
        var finding = Find(Input(target: Van(active: false)), "HandsetInactive");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }

    [Fact]
    public void A_handset_off_a_van_role_cannot_be_given_a_device()
    {
        var finding = Find(Input(target: Van(roleSupports: false)), "RoleCannotHoldDevice");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }

    [Fact]
    public void An_expired_certificate_is_refused()
    {
        var finding = Find(Input(certificateValidTill: Now.AddDays(-1)), "CertificateExpired");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }

    /// <summary>
    /// A certificate that lapses next month still signs today, so this warns. A van out of coverage
    /// cannot be told its certificate expired, which is why it is said at registration rather than left
    /// to be discovered.
    /// </summary>
    [Fact]
    public void A_certificate_expiring_soon_warns_without_refusing()
    {
        var findings = FiscalDeviceRegistration.Inspect(Input(certificateValidTill: Now.AddDays(10)));

        Assert.Equal(FiscalDeviceRegistrationSeverity.Warn, findings.Single().Severity);
        Assert.False(FiscalDeviceRegistration.IsBlocked(findings));
    }

    [Fact]
    public void A_certificate_with_room_to_spare_says_nothing()
    {
        Assert.Null(Find(Input(certificateValidTill: Now.AddDays(90)), "CertificateExpiringSoon"));
    }

    /// <summary>
    /// A device we could not read is refused. Registering one on the strength of a typed number would
    /// hand a van an identity nobody has checked.
    /// </summary>
    [Fact]
    public void A_device_the_platform_could_not_describe_is_refused()
    {
        var finding = Find(
            Input(reachable: false, platformError: "DEV404: no such device for this taxpayer"),
            "PlatformUnreachable");

        Assert.NotNull(finding);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
        Assert.Contains("DEV404", finding.Message);
    }

    /// <summary>
    /// Findings about the handset survive an unreachable platform, because they are true regardless and
    /// the office can act on them while the platform is down.
    /// </summary>
    [Fact]
    public void Handset_problems_are_still_reported_when_the_platform_is_down()
    {
        var findings = FiscalDeviceRegistration.Inspect(
            Input(reachable: false, target: Van(roleSupports: false)));

        Assert.Contains(findings, f => f.Code == "RoleCannotHoldDevice");
        Assert.Contains(findings, f => f.Code == "PlatformUnreachable");
    }

    /// <summary>
    /// Nothing below the id is worth saying about a device that was never named, so the check stops
    /// there rather than reporting an unreachable platform for device 0.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_device_id_that_is_not_a_number_ZIMRA_could_have_issued_is_refused_alone(int deviceId)
    {
        var findings = FiscalDeviceRegistration.Inspect(Input(deviceId: deviceId));

        var finding = Assert.Single(findings);
        Assert.Equal("DeviceIdRequired", finding.Code);
        Assert.Equal(FiscalDeviceRegistrationSeverity.Block, finding.Severity);
    }
}
