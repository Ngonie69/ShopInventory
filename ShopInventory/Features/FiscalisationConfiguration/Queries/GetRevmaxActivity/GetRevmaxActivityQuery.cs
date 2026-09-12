using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxActivity;

/// <summary>
/// What the REVMax device is, and what this application has filed on it over a window.
/// </summary>
/// <remarks>
/// The window defaults to the last 30 days rather than all time. The fiscal transaction log is written
/// on every attempt by every path and is one of the larger tables here; an unbounded default would make
/// the console's slowest query the one nobody asked a question with.
/// </remarks>
public sealed record GetRevmaxActivityQuery(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    int RecentCount = 25
) : IRequest<ErrorOr<RevmaxActivityResult>>;

/// <summary>
/// The device as it answers right now, and the filing this application has recorded against it.
/// </summary>
/// <remarks>
/// <see cref="AttributedToSerial"/> is the honest part of this result. Nothing on a fiscal transaction
/// row names the provider that wrote it — <c>SourceSystem</c> names the call site, not the device — so
/// rows are attributed by the serial the device stamps them with. When the device answers, that serial
/// is known and the figures below are REVMax's alone. When it does not, the serial is null, no filter is
/// applied, and the figures cover whatever filed in the window. Saying which happened is the difference
/// between a number and a number you can act on.
/// </remarks>
public sealed record RevmaxActivityResult(
    string Provider,
    bool IsLiveProvider,
    bool Enabled,
    string BaseUrl,
    int ConfiguredDeviceId,
    RevmaxDeviceDto? Device,
    string? DeviceError,
    string? AttributedToSerial,
    DateTime FromUtc,
    DateTime ToUtc,
    RevmaxTotalsDto Totals,
    List<RevmaxCurrencyTotalDto> ByCurrency,
    List<RevmaxTransactionDto> Recent);

/// <summary>
/// The device's own answer about itself.
/// </summary>
/// <remarks>
/// <see cref="LastFiscalDayNo"/> and <see cref="LastReceiptGlobalNo"/> are the device's counters, not
/// ours, and they count every receipt the box has filed — including the ones the vendor's own SAP add-on
/// files, which never reach our log. A gap between them and <see cref="RevmaxTotalsDto.ReceiptsFiled"/>
/// is expected and is not a fault.
/// </remarks>
public sealed record RevmaxDeviceDto(
    string? DeviceId,
    string? SerialNumber,
    string? CompanyName,
    string? Tin,
    string? Vat,
    string? Bpn,
    string? FiscalDayStatus,
    int? LastFiscalDayNo,
    int? LastReceiptGlobalNo,
    List<RevmaxFactDto> Licence);

/// <summary>
/// One named value read off the device.
/// </summary>
/// <remarks>
/// The licence payload is untyped on the device's own Swagger, and its shape is not ours to fix. Read
/// generically rather than against guessed property names: a licence that renders as whatever the device
/// returned is right in every version of that shape, where a typed one silently renders blank in the
/// first version that differs.
/// </remarks>
public sealed record RevmaxFactDto(string Name, string Value);

/// <summary>
/// Counts over the window.
/// </summary>
/// <remarks>
/// <see cref="ReceiptsFiled"/> counts distinct receipt numbers, so a document filed once and read back
/// twice counts once. <see cref="DocumentsFiled"/> and <see cref="DocumentsFailed"/> count documents by
/// their latest row, which is the same rule the work queue and the invoice list use — a document that
/// failed and was then filed is filed, and must not appear in both.
/// </remarks>
public sealed record RevmaxTotalsDto(
    int ReceiptsFiled,
    int DocumentsFiled,
    int DocumentsFailed,
    int FiscalDaysCovered,
    int? FirstReceiptGlobalNo,
    int? LastReceiptGlobalNo,
    DateTime? FirstAtUtc,
    DateTime? LastAtUtc);

/// <summary>
/// Money filed, per currency.
/// </summary>
/// <remarks>
/// Per currency because these are trading totals and this taxpayer trades in more than one. A single
/// summed figure across USD and ZWG is not a large number, it is a wrong one.
/// </remarks>
public sealed record RevmaxCurrencyTotalDto(
    string Currency,
    int Documents,
    decimal DocTotal,
    decimal VatSum);

public sealed record RevmaxTransactionDto(
    int Id,
    string DocumentType,
    int DocNum,
    string Status,
    int? ReceiptGlobalNo,
    string? FiscalDay,
    string? DeviceSerialNumber,
    string? CardCode,
    string? CardName,
    decimal DocTotal,
    decimal VatSum,
    string? Currency,
    string? VerificationCode,
    string? Message,
    DateTime TimestampUtc,
    bool Filed);
