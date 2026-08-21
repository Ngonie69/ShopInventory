namespace ShopInventory.Common.Fiscalization;

/// <summary>How seriously a registration finding should be taken.</summary>
public enum FiscalDeviceRegistrationSeverity
{
    /// <summary>Worth saying, and does not stop the registration.</summary>
    Note,

    /// <summary>The registration goes through, but something about it will need attention.</summary>
    Warn,

    /// <summary>The registration is refused.</summary>
    Block
}

/// <summary>
/// One thing the office should know before a device is registered to a handset.
/// </summary>
/// <remarks>
/// <c>Code</c> is a stable identifier, so a screen can style a finding without matching on its prose.
/// </remarks>
public sealed record FiscalDeviceRegistrationFinding(
    FiscalDeviceRegistrationSeverity Severity,
    string Code,
    string Message);

/// <summary>The handset a device is being registered to.</summary>
/// <remarks>
/// <c>AlreadyHoldsThisDevice</c> makes the registration a no-op rather than a conflict with itself.
/// </remarks>
public sealed record FiscalDeviceRegistrationTarget(
    string Label,
    bool IsActive,
    bool RoleSupportsDevice,
    bool AlreadyHoldsThisDevice);

/// <summary>Everything the decision needs, gathered by the caller so this stays free of I/O.</summary>
/// <remarks>
/// <para>
/// <c>PinnedDefaultDeviceId</c> is <c>Fiscalisation:DefaultDeviceId</c>. Zero means it is unpinned, which
/// is a finding in itself — see <see cref="FiscalDeviceRegistration"/>.
/// </para>
/// <para>
/// <c>PlatformError</c> says why the platform could not be read, when it could not. Carried rather than
/// swallowed: "device 4 is not registered to this taxpayer" and "the API key is missing" send the office
/// to different places.
/// </para>
/// <para>
/// <c>CurrentHolderLabel</c> is the handset already registered against this device, if any.
/// <c>Target</c> is null to judge the device on its own merits, which is what the screen wants while
/// someone is still typing an id and has not yet said which van it is for.
/// </para>
/// </remarks>
public sealed record FiscalDeviceRegistrationInput(
    int DeviceId,
    int PinnedDefaultDeviceId,
    bool PlatformReachable,
    string? PlatformError,
    string? DeviceSerialNo,
    string? OperatingMode,
    DateTime? CertificateValidTill,
    string? CurrentHolderLabel,
    FiscalDeviceRegistrationTarget? Target,
    DateTime NowUtc);

/// <summary>
/// Whether a ZIMRA fiscal device may be registered to a van handset, and what the office should know
/// either way.
/// </summary>
/// <remarks>
/// <para>
/// Kept free of EF, HTTP and MediatR so the rules are asserted directly in
/// <c>ShopInventory.Tests/FiscalDeviceRegistrationTests.cs</c>. Every refusal here is silent on a
/// handset: the van simply cannot trade offline, days later, in a dead spot, with no one to ask.
/// </para>
/// <para>
/// The two that matter most are the operating mode and the pin. An <c>Online</c> device is one whose
/// sequence FDMS owns; handing it to a handset that signs its own receipts forks the chain. And with
/// <c>Fiscalisation:DefaultDeviceId</c> left at zero, this server submits its own SAP and shop receipts
/// with device id 0, which the platform reads as "walk every configured device until one accepts" — so
/// it can walk onto the very device being registered here.
/// </para>
/// </remarks>
public static class FiscalDeviceRegistration
{
    /// <summary>The only mode whose sequence belongs to the handset rather than to FDMS.</summary>
    public const string HandsetOperatingMode = "Offline";

    /// <summary>How long before expiry a certificate is worth mentioning.</summary>
    private const int CertificateNoticeDays = 30;

    public static IReadOnlyList<FiscalDeviceRegistrationFinding> Inspect(FiscalDeviceRegistrationInput input)
    {
        var findings = new List<FiscalDeviceRegistrationFinding>();

        if (input.DeviceId <= 0)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "DeviceIdRequired",
                "A fiscal device id is the number ZIMRA registered the device under. It has to be a "
                + "positive number."));

            // Nothing below can say anything useful about a device that was never named.
            return findings;
        }

        InspectPin(input, findings);
        InspectHolder(input, findings);
        InspectTarget(input, findings);

        if (!input.PlatformReachable)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "PlatformUnreachable",
                string.IsNullOrWhiteSpace(input.PlatformError)
                    ? $"The Fiscalisation platform could not tell us anything about device {input.DeviceId}. "
                      + "Registering a device we cannot read would hand a van an identity nobody has checked."
                    : $"The Fiscalisation platform could not read device {input.DeviceId}: {input.PlatformError}"));

            return findings;
        }

        InspectOperatingMode(input, findings);
        InspectCertificate(input, findings);

        return findings;
    }

    /// <summary>Whether anything in <paramref name="findings"/> refuses the registration.</summary>
    public static bool IsBlocked(IEnumerable<FiscalDeviceRegistrationFinding> findings) =>
        findings.Any(finding => finding.Severity == FiscalDeviceRegistrationSeverity.Block);

    /// <summary>
    /// The server's own submissions must not be able to reach a handset's device.
    /// </summary>
    /// <remarks>
    /// Two separate problems wearing the same clothes. Registering the pinned device itself is refused
    /// outright — that device is where this server posts shop and SAP receipts, and a handset signing
    /// into it forks the chain from both ends at once. Leaving the pin at zero is not refused, because
    /// it is a deployment setting the office may not be able to change from this screen, but it is said
    /// plainly: a server that names no device walks every one of them, including this.
    /// </remarks>
    private static void InspectPin(
        FiscalDeviceRegistrationInput input,
        List<FiscalDeviceRegistrationFinding> findings)
    {
        if (input.PinnedDefaultDeviceId > 0 && input.PinnedDefaultDeviceId == input.DeviceId)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "DeviceIsTheServersOwn",
                $"Device {input.DeviceId} is the one this server fiscalises its own shop and SAP receipts "
                + "on. A handset signing into that same chain forks it, and ZIMRA refuses the whole "
                + "fiscal day. Register the van on a device of its own."));

            return;
        }

        if (input.PinnedDefaultDeviceId <= 0)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Warn,
                "DefaultDeviceUnpinned",
                "This server has no fiscal device pinned, so it submits its own receipts with device id 0 "
                + "and the platform walks every configured device until one accepts — including this one. "
                + "Set Fiscalisation__DefaultDeviceId to the shop's Online device before a van signs "
                + "offline on this one."));
        }
    }

    /// <summary>
    /// Whether this device is already spoken for.
    /// </summary>
    /// <remarks>
    /// Said even when no handset has been picked yet, because "device 3 belongs to VAN002" is the single
    /// most useful thing this screen can tell someone who has just typed 3.
    /// </remarks>
    private static void InspectHolder(
        FiscalDeviceRegistrationInput input,
        List<FiscalDeviceRegistrationFinding> findings)
    {
        if (input.Target?.AlreadyHoldsThisDevice == true)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Note,
                "AlreadyRegistered",
                $"{input.Target.Label} already signs as device {input.DeviceId}. Saving changes nothing."));

            return;
        }

        if (string.IsNullOrWhiteSpace(input.CurrentHolderLabel))
        {
            return;
        }

        // Held by someone else. A block once a target is named; before that it is only a fact about the
        // device, and blocking on it would refuse a registration nobody has asked for yet.
        findings.Add(new(
            input.Target is null
                ? FiscalDeviceRegistrationSeverity.Warn
                : FiscalDeviceRegistrationSeverity.Block,
            "HeldByAnotherHandset",
            $"Device {input.DeviceId} is already registered to {input.CurrentHolderLabel}. A device's "
            + "receipt chain has exactly one writer: two handsets on one id each sign a different "
            + "receipt as the same number. Clear it there first."));
    }

    private static void InspectTarget(
        FiscalDeviceRegistrationInput input,
        List<FiscalDeviceRegistrationFinding> findings)
    {
        if (input.Target is not { } target)
        {
            return;
        }

        if (!target.IsActive)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "HandsetInactive",
                $"{target.Label} is not an active account, so it cannot collect a lease for this device."));
        }

        if (!target.RoleSupportsDevice)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "RoleCannotHoldDevice",
                $"{target.Label} is not on a van role. A fiscal device is registered to a handset "
                + "that sells, so only Sales and ADR accounts can carry one."));
        }
    }

    private static void InspectOperatingMode(
        FiscalDeviceRegistrationInput input,
        List<FiscalDeviceRegistrationFinding> findings)
    {
        var mode = input.OperatingMode?.Trim();

        if (string.IsNullOrWhiteSpace(mode))
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "OperatingModeUnknown",
                $"The platform did not say which mode device {input.DeviceId} is registered in. Only an "
                + "Offline-mode device may sign its own receipts, and we will not assume this is one."));

            return;
        }

        if (!string.Equals(mode, HandsetOperatingMode, StringComparison.OrdinalIgnoreCase))
        {
            var serial = string.IsNullOrWhiteSpace(input.DeviceSerialNo)
                ? $"Device {input.DeviceId}"
                : $"Device {input.DeviceId} ({input.DeviceSerialNo})";

            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "WrongOperatingMode",
                $"{serial} is registered with ZIMRA in {mode} mode. In Online mode FDMS owns the receipt "
                + "sequence, so a handset's own numbering is not the device's — this is a server device, "
                + "not a van's."));
        }
    }

    private static void InspectCertificate(
        FiscalDeviceRegistrationInput input,
        List<FiscalDeviceRegistrationFinding> findings)
    {
        if (input.CertificateValidTill is not { } validTill)
        {
            return;
        }

        if (validTill <= input.NowUtc)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Block,
                "CertificateExpired",
                $"This device's ZIMRA certificate expired on {validTill:d MMM yyyy}. It cannot sign "
                + "anything until the certificate is renewed on the Fiscalisation platform."));

            return;
        }

        var daysLeft = (int)Math.Floor((validTill - input.NowUtc).TotalDays);

        if (daysLeft <= CertificateNoticeDays)
        {
            findings.Add(new(
                FiscalDeviceRegistrationSeverity.Warn,
                "CertificateExpiringSoon",
                $"This device's ZIMRA certificate expires on {validTill:d MMM yyyy}, in {daysLeft} day(s). "
                + "A van out of coverage cannot be told its certificate lapsed, so renew it before then."));
        }
    }
}
