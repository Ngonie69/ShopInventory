namespace ShopInventory.DTOs;

/// <summary>A handset that could carry a fiscal device, as the office picks from.</summary>
public class FiscalDeviceHandsetDto
{
    public Guid UserId { get; set; }

    /// <summary>Named the way a refused rep would be told — van code first. See OfflineSigningLeaseMapper.</summary>
    public string Label { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    /// <summary>What this handset already signs as, or null if it has never been registered.</summary>
    public int? FiscalDeviceId { get; set; }
}

/// <summary>One thing the office should know before registering a device.</summary>
public class FiscalDeviceRegistrationFindingDto
{
    /// <summary><c>Note</c>, <c>Warn</c> or <c>Block</c>.</summary>
    public string Severity { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// What the Fiscalisation platform says about a device, and whether it may be handed to a van.
/// </summary>
/// <remarks>
/// Deliberately answered for devices this application has never heard of — that is the whole point of
/// the screen. Nothing here is stored; it is read from the platform each time it is asked for.
/// </remarks>
public class FiscalDevicePreviewDto
{
    public int DeviceId { get; set; }

    /// <summary>False when the platform could not be read, which refuses the registration.</summary>
    public bool Reachable { get; set; }

    public string? PlatformError { get; set; }

    public string? SerialNumber { get; set; }

    public string? BranchName { get; set; }

    /// <summary><c>Online</c> or <c>Offline</c> as ZIMRA registered it. Only Offline may go to a van.</summary>
    public string? OperatingMode { get; set; }

    public string? TaxPayerName { get; set; }

    public DateTime? CertificateValidTill { get; set; }

    public int? CertificateDaysRemaining { get; set; }

    public int? FiscalDayNo { get; set; }

    public string? FiscalDayStatus { get; set; }

    /// <summary>The handset already registered against this device, if any.</summary>
    public Guid? CurrentHolderUserId { get; set; }

    public string? CurrentHolderLabel { get; set; }

    /// <summary>Whether the pin is set, so the screen can say why an unpinned server is a risk.</summary>
    public int PinnedDefaultDeviceId { get; set; }

    /// <summary>
    /// Whether the chosen handset may be given this device. False when no handset is chosen, or when
    /// anything in <see cref="Findings"/> refuses it.
    /// </summary>
    public bool CanRegister { get; set; }

    /// <summary>
    /// Whether the device can be taken off whoever holds it.
    /// </summary>
    /// <remarks>
    /// Deliberately not gated on the platform or on <see cref="Findings"/>. A release is how the office
    /// recovers a device from a handset that is lost, broken or wrongly registered, and every one of
    /// those is a moment when the platform may also be unreachable or the device may look wrong. What
    /// still guards it is the outgoing handset's queue, which the save reports as a conflict.
    /// </remarks>
    public bool CanRelease { get; set; }

    public List<FiscalDeviceRegistrationFindingDto> Findings { get; set; } = [];
}

/// <summary>Registers the handset that signs as a fiscal device, or clears it.</summary>
public class RegisterFiscalDeviceHandsetRequest
{
    /// <summary>
    /// The handset to register, or null to clear this device from whoever currently holds it — which is
    /// how a device is moved between vans, since only one account may carry it at a time.
    /// </summary>
    public Guid? HandsetUserId { get; set; }
}
