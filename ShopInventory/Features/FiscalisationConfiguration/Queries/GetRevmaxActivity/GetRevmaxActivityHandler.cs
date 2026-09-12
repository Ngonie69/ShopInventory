using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxActivity;

/// <summary>
/// Reads the REVMax device and this application's filing against it.
/// </summary>
/// <remarks>
/// Read-only, and deliberately only the three read routes. <c>ZReport</c> closes the fiscal day — it is
/// not a report in the sense its name suggests, and a console that called it to render a panel would
/// close the taxpayer's day every time someone opened the page.
///
/// The device calls are best-effort. A device that will not answer still returns a result, carrying the
/// reason: the filing figures come from our own database and are perfectly readable while the device is
/// unreachable, and blanking the whole section would turn a device outage into an apparent absence of
/// activity.
/// </remarks>
public sealed class GetRevmaxActivityHandler(
    ApplicationDbContext db,
    IRevmaxClient client,
    IOptionsMonitor<RevmaxSettings> revmaxSettings,
    IOptionsMonitor<FiscalisationSettings> fiscalisationSettings,
    ILogger<GetRevmaxActivityHandler> logger
) : IRequestHandler<GetRevmaxActivityQuery, ErrorOr<RevmaxActivityResult>>
{
    /// <summary>How far back the window reaches when none is given.</summary>
    private const int DefaultWindowDays = 30;

    /// <summary>Rows the recent list may carry.</summary>
    private const int MaxRecentCount = 200;

    /// <summary>
    /// How long the console waits on the device before giving up on it.
    /// </summary>
    /// <remarks>
    /// Far below <c>Revmax:TimeoutSeconds</c>, and deliberately: that timeout belongs to filing a
    /// receipt, where waiting is right because the alternative is an unresolved submission. This is a
    /// page read, where waiting is wrong — an unreachable device would otherwise hold the whole console
    /// for a minute and a half and then show the same thing it shows at eight seconds.
    /// </remarks>
    private static readonly TimeSpan DeviceReadTimeout = TimeSpan.FromSeconds(8);

    public async Task<ErrorOr<RevmaxActivityResult>> Handle(
        GetRevmaxActivityQuery query,
        CancellationToken cancellationToken)
    {
        var revmax = revmaxSettings.CurrentValue;
        var fiscalisation = fiscalisationSettings.CurrentValue;

        // Stamped Utc, and that is not decoration. TimestampUtc is `timestamp with time zone`, and a date
        // bound from a query string arrives with Kind=Unspecified; Npgsql refuses to compare one against
        // the other and throws rather than returning a wrong row set. The SQLite suite cannot see this —
        // it compares the two happily — so the Kind has to be set here rather than relied on from a test.
        var toUtc = query.ToDate is { } to
            ? DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Utc)
            : DateTime.UtcNow.Date.AddDays(1);

        var fromUtc = query.FromDate is { } from
            ? DateTime.SpecifyKind(from.Date, DateTimeKind.Utc)
            : toUtc.AddDays(-DefaultWindowDays);

        if (fromUtc >= toUtc)
        {
            return Error.Validation(
                "FiscalisationConsole.RevmaxWindowInverted",
                "The 'from' date must fall before the 'to' date.");
        }

        var recentCount = Math.Clamp(query.RecentCount, 1, MaxRecentCount);

        var (device, deviceError) = revmax.Enabled
            ? await ReadDeviceAsync(cancellationToken)
            : (null, "REVMax is disabled in configuration (Revmax:Enabled), so the device was not asked.");

        // Only the device can tell us which serial its receipts carry, and that serial is the only thing
        // on a row that distinguishes this device's filing from another provider's. Without it the
        // figures are reported unfiltered rather than guessed at.
        var serial = string.IsNullOrWhiteSpace(device?.SerialNumber) ? null : device.SerialNumber;

        var latestPerDocument = LatestRowPerDocument()
            .Where(transaction => transaction.TimestampUtc >= fromUtc && transaction.TimestampUtc < toUtc)
            .Where(transaction => transaction.DocNum > 0);

        var windowRows = db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.TimestampUtc >= fromUtc && transaction.TimestampUtc < toUtc);

        if (serial is not null)
        {
            // A row the device stamped, or one that never got a serial because the filing failed before
            // the device answered. Dropping the unstamped ones would hide every failure, which is the
            // half of this section a person is most likely to have come for.
            latestPerDocument = latestPerDocument.Where(transaction =>
                transaction.DeviceSerialNumber == serial
                || transaction.DeviceSerialNumber == null
                || transaction.DeviceSerialNumber == "");

            windowRows = windowRows.Where(transaction => transaction.DeviceSerialNumber == serial);
        }

        var filedDocuments = latestPerDocument.Where(FiscalDocumentStatusProjector.HasFiscalEvidenceExpression);

        var documentsFiled = await filedDocuments.CountAsync(cancellationToken);

        var documentsFailed = await latestPerDocument
            .CountAsync(FiscalDocumentStatusProjector.LacksFiscalEvidenceExpression, cancellationToken);

        var receiptsFiled = await windowRows
            .Where(transaction => transaction.ReceiptGlobalNo != null)
            .Select(transaction => transaction.ReceiptGlobalNo)
            .Distinct()
            .CountAsync(cancellationToken);

        var fiscalDaysCovered = await windowRows
            .Where(transaction => transaction.FiscalDay != null && transaction.FiscalDay != "")
            .Select(transaction => transaction.FiscalDay)
            .Distinct()
            .CountAsync(cancellationToken);

        var receiptNumbers = windowRows.Where(transaction => transaction.ReceiptGlobalNo != null);

        var firstReceipt = await receiptNumbers.MinAsync(t => t.ReceiptGlobalNo, cancellationToken);
        var lastReceipt = await receiptNumbers.MaxAsync(t => t.ReceiptGlobalNo, cancellationToken);

        var firstAt = await filedDocuments.MinAsync(t => (DateTime?)t.TimestampUtc, cancellationToken);
        var lastAt = await filedDocuments.MaxAsync(t => (DateTime?)t.TimestampUtc, cancellationToken);

        // Grouped into an anonymous type rather than straight into the DTO: the provider will not
        // translate a constructor call inside a GroupBy projection, and the whole query fails rather
        // than falling back to the client.
        var currencyRows = await filedDocuments
            .GroupBy(transaction => transaction.Currency)
            .Select(group => new
            {
                Currency = group.Key,
                Documents = group.Count(),
                DocTotal = group.Sum(transaction => transaction.DocTotal),
                VatSum = group.Sum(transaction => transaction.VatSum)
            })
            .ToListAsync(cancellationToken);

        var byCurrency = currencyRows
            .Select(row => new RevmaxCurrencyTotalDto(
                row.Currency ?? string.Empty,
                row.Documents,
                row.DocTotal,
                row.VatSum))
            .OrderByDescending(total => total.DocTotal)
            .ToList();

        var recentRows = await latestPerDocument
            .OrderByDescending(transaction => transaction.TimestampUtc)
            .ThenByDescending(transaction => transaction.Id)
            .Take(recentCount)
            .ToListAsync(cancellationToken);

        var recent = recentRows
            .Select(transaction => new RevmaxTransactionDto(
                transaction.Id,
                transaction.DocumentType,
                transaction.DocNum,
                transaction.Status,
                transaction.ReceiptGlobalNo,
                transaction.FiscalDay,
                transaction.DeviceSerialNumber,
                transaction.CardCode,
                transaction.CardName,
                transaction.DocTotal,
                transaction.VatSum,
                transaction.Currency,
                transaction.VerificationCode,
                transaction.Message,
                transaction.TimestampUtc,
                // The same rule the counts above used, compiled from the same expression, so a row can
                // never be listed as failed under a heading that counted it as filed.
                FiscalDocumentStatusProjector.HasFiscalEvidencePredicate(transaction)))
            .ToList();

        return new RevmaxActivityResult(
            fiscalisation.Provider.ToString(),
            !fiscalisation.UsesPlatform,
            revmax.Enabled,
            revmax.BaseUrl,
            revmax.DefaultRefDeviceId,
            device,
            deviceError,
            serial,
            fromUtc,
            toUtc,
            new RevmaxTotalsDto(
                receiptsFiled,
                documentsFiled,
                documentsFailed,
                fiscalDaysCovered,
                firstReceipt,
                lastReceipt,
                firstAt,
                lastAt),
            byCurrency,
            recent);
    }

    /// <summary>
    /// One row per document — the most recently synced one.
    /// </summary>
    /// <remarks>
    /// The same shape the work queue uses, and for the same reason: every attempt writes a row, so a
    /// document retried three times is three rows and counting rows would report it as three documents.
    /// The tie-break on <c>Id</c> matters because a retry inside the same clock tick is common.
    /// </remarks>
    private IQueryable<DesktopFiscalTransactionEntity> LatestRowPerDocument()
        => db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => !db.DesktopFiscalTransactions.Any(later =>
                later.DocumentType == transaction.DocumentType &&
                later.DocNum == transaction.DocNum &&
                (later.LastSyncedAtUtc > transaction.LastSyncedAtUtc ||
                    (later.LastSyncedAtUtc == transaction.LastSyncedAtUtc && later.Id > transaction.Id))));

    /// <summary>
    /// Asks the device who it is and where its fiscal day has got to.
    /// </summary>
    /// <remarks>
    /// Three reads, and never a fourth: <c>ZReport</c> closes the day.
    ///
    /// An empty <c>Data</c> alongside a refusal code is the device's routine "Init error -1" — the card is
    /// momentarily busy while a till fiscalises. It is a transient state, not an outage, so it is reported
    /// as the device being busy rather than as a failure of this page.
    /// </remarks>
    private async Task<(RevmaxDeviceDto? Device, string? Error)> ReadDeviceAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DeviceReadTimeout);

        try
        {
            var card = await client.GetCardDetailsAsync(deadline.Token);
            var day = await client.GetDayStatusAsync(deadline.Token);
            var licence = await client.GetLicenseAsync(deadline.Token);

            if (card is null && day is null)
            {
                return (null, "The REVMax device did not answer. The filing figures below are from this system's own log.");
            }

            var device = new RevmaxDeviceDto(
                card?.DeviceID ?? day?.DeviceID,
                card?.DeviceSerialNumber ?? day?.DeviceSerialNumber,
                card?.Data?.COMPANYNAME,
                card?.Data?.TIN,
                card?.Data?.VAT,
                card?.Data?.BPN,
                day?.Data?.FiscalDayStatus,
                day?.Data?.LastFiscalDayNo,
                day?.Data?.LastReceiptGlobalNo,
                ReadLicenceFacts(licence?.Data));

            var busy = card?.Data is null && day?.Data is null;

            return (device, busy
                ? "The device answered but returned no detail — it is momentarily busy fiscalising. Try again in a moment."
                : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline, not the caller going away. Distinguished because the two mean opposite
            // things: this one is a device that is slow and a page that must still render, the other is
            // a reader who has already navigated on.
            logger.LogWarning(
                "The REVMax device did not answer within {Seconds}s for the fiscalisation console",
                DeviceReadTimeout.TotalSeconds);

            return (null,
                $"The REVMax device did not answer within {DeviceReadTimeout.TotalSeconds:N0} seconds.");
        }
        catch (Exception ex)
        {
            // Never fatal to the section. The log below is readable without the device, and a fiscal
            // console that shows nothing during a device outage is the failure this page exists to avoid.
            logger.LogWarning(ex, "The REVMax device could not be read for the fiscalisation console");
            return (null, $"The REVMax device could not be reached: {ex.Message}");
        }
    }

    /// <summary>Flattens whatever the licence route returned into name/value pairs.</summary>
    /// <remarks>
    /// The licence payload is untyped on the device's own Swagger and its shape is not ours to fix.
    /// Reading it generically renders correctly whatever the device returns, where a typed read would
    /// silently render blank in the first version whose field names differ.
    /// </remarks>
    private static List<RevmaxFactDto> ReadLicenceFacts(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
        {
            return [];
        }

        return element
            .EnumerateObject()
            .Where(property => property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            .Select(property => new RevmaxFactDto(
                property.Name,
                property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString()))
            .Where(fact => !string.IsNullOrWhiteSpace(fact.Value))
            .ToList();
    }
}
