using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationConsoleDevices;

/// <summary>
/// Every fiscal device this application knows about, as the console draws it.
/// </summary>
/// <remarks>
/// Deliberately one call for the whole section. The answer for a single device is stitched from four
/// places — the platform's device configuration, its live fiscal day status, the offline signing
/// nomination and the receipts still queued locally — and asking for them separately would let the page
/// render a device whose serial came from one moment and whose backlog came from another.
/// </remarks>
public sealed record GetFiscalisationConsoleDevicesQuery
    : IRequest<ErrorOr<List<FiscalConsoleDeviceDto>>>;

/// <summary>
/// One fiscal device: what ZIMRA says about it, and what is waiting for it here.
/// </summary>
/// <remarks>
/// <c>Reachable</c> is whether the platform answered. False means every ZIMRA-side field is unset and
/// <c>PlatformError</c> says why — which is not the same as a device with nothing to report.
///
/// <c>OperatingMode</c> is <c>Online</c> or <c>Offline</c> as ZIMRA registered it, and it decides which
/// path a receipt may take: an Offline device signs for itself and the platform archives what it signed,
/// an Online device has its sequence assigned by FDMS. A device never does both.
///
/// <c>FiscalDayHoursElapsed</c> is measured against <c>TaxPayerDayMaxHrs</c>. ZIMRA refuses a day that
/// runs past its limit, and the refusal lands when the file is uploaded — hours after the last receipt
/// was printed and handed over.
///
/// <c>AwaitingHandover</c> counts signed receipts this application is holding that the platform has not
/// taken yet. Not a backlog in the ordinary sense: until these are archived, the fiscal day they belong
/// to cannot be closed without stranding them outside the offline file.
///
/// <c>ChainBroken</c> means the device is stopped. One receipt did not continue the chain, and every
/// receipt this handset signed after it is behind that one — resending cannot fix any of them.
/// </remarks>
public sealed record FiscalConsoleDeviceDto(
    int DeviceId,
    bool Reachable,
    string? PlatformError,
    string? SerialNumber,
    string? BranchName,
    string? OperatingMode,
    DateTime? CertificateValidTill,
    int? CertificateDaysRemaining,
    int? TaxPayerDayMaxHrs,
    int? FiscalDayNo,
    string? FiscalDayStatus,
    DateTime? FiscalDayOpened,
    double? FiscalDayHoursElapsed,
    int? FiscalDayPercentOfMax,
    DateTime? LastReceiptDate,
    int? LastReceiptGlobalNo,
    int? LastReceiptCounter,
    string? OfflineSigningHolder,
    int? OfflineSigningHolderPendingSales,
    DateTime? OfflineSigningHolderLastSeenAtUtc,
    int AwaitingHandover,
    int FailedHandover,
    int Unsignable,
    int Unstamped,
    bool ChainBroken,
    int? ChainBrokenAtReceiptGlobalNo,
    string? ChainBrokenError,
    int BlockedBehindChainBreak);
