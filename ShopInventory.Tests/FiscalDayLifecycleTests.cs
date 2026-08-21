using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The walk from a receipt a handset stamped to ZIMRA actually holding it.
/// </summary>
/// <remarks>
/// Every test here guards a step that cannot be taken back. A fiscal day closed over a receipt the platform
/// never received strands that receipt for good — no later file can carry it, and the customer keeps a
/// fiscal receipt for a sale the revenue authority is never told about. A close or an upload repeated after
/// an unknown outcome is not idempotent at FDMS. So the assertions that matter are mostly negative ones:
/// what the lifecycle did <em>not</em> do.
/// </remarks>
public sealed class FiscalDayLifecycleTests : IDisposable
{
    private const int DeviceId = 42;
    private const int FiscalDayNo = 7;

    /// <summary>
    /// The same day as <see cref="FiscalDayNo"/>, as the sales table stores it — a free-text column, which
    /// is the whole reason two of the tests below can write it two ways. A const because a default
    /// parameter value has to be one.
    /// </summary>
    private const string FiscalDayNoText = "7";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingAdminClient _admin = new();
    private readonly StubDeviceConfigCache _deviceConfig = new();
    private readonly StubFiscalisationClient _platform = new();

    /// <summary>
    /// A stated instant rather than the real clock. Every decision the lifecycle makes is a comparison
    /// against the time — whether the close hour has passed, whether the day is over its permitted length —
    /// so a test running at 23:45 would otherwise assert something different from one running at noon.
    /// </summary>
    private static readonly DateTime NowUtc = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The taxpayer's wall clock, which is the clock a fiscal day is measured in. Converting it would move
    /// receipts across the day boundary, so the fixtures are built in it rather than in UTC.
    /// </summary>
    private static DateTime NowLocal => AuditService.ToCAT(NowUtc);

    public FiscalDayLifecycleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── The rule that overrides everything ──────────────────────────────────

    /// <summary>
    /// The one test worth keeping if every other were deleted. Those receipt numbers are inside this day;
    /// once it is closed FDMS will not accept them and no later file can carry them.
    /// </summary>
    [Fact]
    public async Task A_day_is_not_closed_while_a_signed_receipt_is_still_outstanding()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Pending);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Empty(_admin.Submits);
        Assert.Empty(_admin.Closes);

        Assert.Equal(1, result.DaysBlockedByOutstandingReceipts);
        Assert.Equal(1, result.OutstandingReceipts);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.Open, state.Status);
        Assert.Contains("not reached the platform", state.LastError);
    }

    /// <summary>
    /// A receipt that was never stamped consumed no number off the device's sequence, so nothing is queued
    /// behind it. Counting it as outstanding would hold the day of every van still on a pre-signing build.
    /// </summary>
    [Fact]
    public async Task An_unstamped_sale_does_not_hold_the_day_open()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Unstamped);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(0, result.DaysBlockedByOutstandingReceipts);
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// A receipt arriving after the day was drained walks the day back rather than being left behind. The
    /// drain is a fact about the receipts, not a decision taken once.
    /// </summary>
    [Fact]
    public async Task A_receipt_arriving_after_the_drain_reopens_the_day()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.Drained);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Pending);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Equal(FiscalDayLifecycleStatus.Open, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// The gate cannot be keyed on (device, day) alone, because a receipt can spend a chain number without
    /// carrying either column. A sale claims a sequence on its global number alone while carrying a
    /// signature also needs a fiscal day, so the ingest writes an Unsignable row with a null day whenever
    /// the two disagree — and a query keyed on the day number cannot see it. Its number is still inside
    /// this day.
    /// </summary>
    [Fact]
    public async Task A_receipt_that_spent_a_chain_number_but_names_no_day_still_holds_its_device()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Unsignable, fiscalDayNo: null, receiptGlobalNo: 501);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Empty(_admin.Submits);
        Assert.Equal(1, result.DaysBlockedByOutstandingReceipts);
        Assert.Equal(1, result.ChainHoleReceipts);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.Open, state.Status);
        Assert.Contains("took a chain number", state.LastError);
    }

    /// <summary>
    /// A chain hole is a hole whatever day it claims: the platform will not accept the receipt after it, so
    /// nothing behind it can be handed over and no later day on that device can close over it either.
    /// </summary>
    [Fact]
    public async Task A_chain_hole_from_another_day_still_holds_this_one()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale("VAN006-old", DesktopSaleReceiptIngestStatus.ChainBroken, fiscalDayNo: "6", receiptGlobalNo: 499);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Equal(FiscalDayLifecycleStatus.Open, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// A receipt whose device string did not parse has a number off <em>some</em> device's chain and no way
    /// to say whose. It might be this one's, so it holds every device until a person says otherwise — and
    /// it is reported as its own thing, because nothing automatic will ever clear it.
    /// </summary>
    [Fact]
    public async Task A_receipt_that_cannot_be_attributed_to_a_device_holds_every_device()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale(
            "VAN006-orphan",
            DesktopSaleReceiptIngestStatus.Unsignable,
            fiscalDeviceId: null,
            fiscalDayNo: null,
            receiptGlobalNo: 900);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Empty(_admin.Submits);
        Assert.Equal(1, result.UnattributableReceipts);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.Open, state.Status);
        Assert.Contains("name no device", state.LastError);
    }

    /// <summary>
    /// The count taken at the top of the pass is filtered to the discovery window; the day being closed is
    /// not. A day trading either side of that window would close over the difference, so the gate re-reads
    /// with no window at all, immediately before the irreversible step.
    /// </summary>
    [Fact]
    public async Task An_outstanding_receipt_older_than_the_discovery_window_still_stops_the_close()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale(
            "VAN006-old",
            DesktopSaleReceiptIngestStatus.Pending,
            docDate: NowUtc.Date.AddDays(-60));
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Equal(1, result.DaysBlockedByOutstandingReceipts);
        Assert.Equal(FiscalDayLifecycleStatus.Open, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// The pass makes network calls per device, so a receipt can land after the counts were read and before
    /// this device's turn. Reading once at the start and closing on it is closing on a number that was true
    /// several minutes and several devices ago.
    /// </summary>
    [Fact]
    public async Task A_receipt_that_lands_during_the_pass_still_stops_the_close()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        // The configuration lookup is the first thing the per-device step does, so this is a receipt
        // arriving after the pass began and before this device was reached.
        _deviceConfig.OnLookup = _ =>
        {
            if (_context.DesktopSales.All(sale => sale.ExternalReferenceId != "VAN006-late"))
            {
                AddSale("VAN006-late", DesktopSaleReceiptIngestStatus.Pending, receiptGlobalNo: 501);
                _context.SaveChanges();
            }
        };

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Empty(_admin.Submits);
        Assert.Equal(1, result.DaysBlockedByOutstandingReceipts);
        Assert.Equal(FiscalDayLifecycleStatus.Open, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// The day number is a free-text column grouped as written and keyed as parsed, so " 7" and "7" arrive
    /// as two groups for one key. Keeping the last one loses the other's receipts from the day's tally.
    /// </summary>
    [Fact]
    public async Task Receipts_written_against_the_same_day_two_ways_are_counted_together()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Ingested, receiptGlobalNo: 501);
        AddSale("VAN006-3", DesktopSaleReceiptIngestStatus.Ingested, fiscalDayNo: " 7", receiptGlobalNo: 502);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        var state = Assert.Single(await _context.FiscalDayStates.AsNoTracking().ToListAsync());
        Assert.Equal(FiscalDayNo, state.FiscalDayNo);
        Assert.Equal(3, state.IngestedReceiptCount);
    }

    // ── The offline route, which is the one every van takes ─────────────────

    /// <summary>
    /// A van handset's device is registered Offline, and the platform refuses <c>CloseDay</c> for one
    /// outright. Its day is closed by the declaration inside the last file, so a close call here would be
    /// both wrong and refused.
    /// </summary>
    [Fact]
    public async Task An_offline_device_closes_its_day_inside_the_file_rather_than_through_CloseDay()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Closes);
        var generate = Assert.Single(_admin.Generates);
        Assert.True(generate.CloseFiscalDay);
        // Re-packaging receipts an earlier file already carried would send them to FDMS twice.
        Assert.False(generate.IncludeAlreadyPackaged);
        Assert.Single(_admin.Submits);
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// The close a handset signed travels with the day's last file. This is the only way a van's day can
    /// close: the platform holds that device's certificate and not its key, so it can verify this
    /// declaration but never produce one.
    /// </summary>
    [Fact]
    public async Task A_handsets_signed_close_is_forwarded_with_the_day()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        HoldSignedClose(
            """{"Counters":[{"FiscalCounterType":"SaleByTax","FiscalCounterCurrency":"USD","FiscalCounterTaxID":1,"FiscalCounterTaxPercent":15.00,"FiscalCounterValue":200.00}],"SignatureHash":"aGFzaA==","SignatureValue":"c2lnbmF0dXJl"}""");
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        var generate = Assert.Single(_admin.Generates);
        Assert.NotNull(generate.DeclaredClose);
        Assert.Equal("c2lnbmF0dXJl", generate.DeclaredClose!.SignatureValue);

        var counter = Assert.Single(generate.DeclaredClose.Counters);
        Assert.Equal("SaleByTax", counter.FiscalCounterType);
        Assert.Equal(200.00m, counter.FiscalCounterValue);
    }

    /// <summary>
    /// Null is the right answer for every device the platform signs for, and it must stay null rather than
    /// becoming an empty declaration — which would assert the day sold nothing.
    /// </summary>
    [Fact]
    public async Task A_day_with_no_held_close_is_offered_without_one()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Null(Assert.Single(_admin.Generates).DeclaredClose);
    }

    /// <summary>
    /// A stored close that will not parse must not take the run down with it: one malformed row would
    /// otherwise stop every other device's day from advancing. The platform then refuses the close, so the
    /// day stalls visibly instead of closing on totals nobody signed.
    /// </summary>
    [Fact]
    public async Task A_held_close_that_cannot_be_read_does_not_stop_the_run()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        HoldSignedClose("{ this is not json");
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Null(Assert.Single(_admin.Generates).DeclaredClose);
    }

    /// <summary>
    /// Seeds the day's state row carrying a close the handset already signed, which is how the row looks
    /// by the time the day is ready to package: the close arrives while the day is still open.
    /// </summary>
    private void HoldSignedClose(string json)
    {
        _context.FiscalDayStates.Add(new FiscalDayStateEntity
        {
            DeviceId = DeviceId,
            FiscalDayNo = FiscalDayNo,
            OpenedAtLocal = NowLocal.Date.AddHours(6),
            Status = FiscalDayLifecycleStatus.Open,
            DeclaredCloseJson = json,
            DeclaredCloseReceivedAtUtc = NowUtc,
            CreatedAt = NowUtc,
            UpdatedAt = NowUtc
        });
    }

    /// <summary>Only a day whose files FDMS accepted cleanly is recorded as being with ZIMRA.</summary>
    [Fact]
    public async Task A_day_reaches_ZIMRA_only_once_every_file_is_settled()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, state.Status);
        Assert.NotNull(state.FileSubmittedAtUtc);
        Assert.Equal("op-settled", state.OfflineFileReference);
        Assert.Null(state.LastError);
    }

    // ── An outcome nobody knows ─────────────────────────────────────────────

    /// <summary>
    /// The platform answering 409 <c>FdmsOperationIndeterminate</c> means FDMS may or may not hold the file.
    /// Sending it again is the one action that cannot be taken back, so the day stops and the next pass
    /// only reads.
    /// </summary>
    [Fact]
    public async Task An_indeterminate_upload_stops_the_day_and_is_never_repeated()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        _admin.OnSubmit = _ => throw Indeterminate("FDMS did not answer the file upload.");

        var first = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(1, first.NeedsReconciliation);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);

        // FDMS is still saying nothing useful, so the second pass must read and stop, not upload again.
        _admin.OnFileStatus = _ => new GetFileStatusApiResponse();
        var submitsSoFar = _admin.Submits.Count;

        var second = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(submitsSoFar, _admin.Submits.Count);
        Assert.Single(_admin.FileStatusReads);
        Assert.Equal(1, second.NeedsReconciliation);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// The same rule on the online route. An Online-mode device's day is closed through FDMS
    /// <c>CloseDay</c>, and closing a fiscal day twice is not idempotent there either.
    /// </summary>
    [Fact]
    public async Task An_indeterminate_close_stops_the_day_and_is_never_repeated()
    {
        _deviceConfig.OperatingMode = "Online";
        AddSale("TILL-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        _admin.OnClose = _ => throw Indeterminate("FDMS did not answer the close.");

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Closes);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);

        // FDMS cannot say yet, so nothing changes and nothing is closed a second time.
        _platform.FiscalDayStatus = "FiscalDayCloseInitiated";

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Closes);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// A timeout is the commonest way an outcome becomes unknown and the one the platform cannot tell us
    /// about, because it never answered. Treated as an ordinary failure the day stays at FileGenerated and
    /// the next pass uploads the same file again — to an FDMS that may already hold it.
    /// </summary>
    [Fact]
    public async Task An_upload_that_times_out_is_reconciled_rather_than_sent_again()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        _admin.OnSubmitThrow = _ => new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 90 seconds elapsing.");

        var first = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Submits);
        Assert.Equal(1, first.NeedsReconciliation);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, state.Status);
        Assert.Contains("unknown", state.LastError);

        // The next pass reads and stops. Uploading again is the one action that cannot be taken back.
        _admin.OnSubmitThrow = null;
        _admin.OnFileStatus = _ => new GetFileStatusApiResponse();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Submits);
        Assert.Single(_admin.FileStatusReads);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// A 503 comes from whatever is in front of the platform, which may already have forwarded the request.
    /// It says nothing about whether FDMS holds the file, so it is an unknown outcome, not a refusal.
    /// </summary>
    [Fact]
    public async Task An_upload_refused_with_503_is_reconciled_rather_than_sent_again()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        _admin.OnSubmitThrow = _ => new FiscalisationApiException(
            HttpStatusCode.ServiceUnavailable, null, "Service Unavailable", hasProblemDocument: false);

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Submits);
        Assert.Equal(1, result.NeedsReconciliation);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// The same rule on the close, which is the other operation FDMS does not accept twice.
    /// </summary>
    [Fact]
    public async Task A_close_that_times_out_is_reconciled_rather_than_closed_again()
    {
        _deviceConfig.OperatingMode = "Online";
        AddSale("TILL-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        _admin.OnCloseThrow = _ => new TaskCanceledException("The request was canceled.");

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Closes);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);

        // FDMS still cannot say, so the second pass reads and leaves it. It does not close again.
        _admin.OnCloseThrow = null;
        _platform.FiscalDayStatus = "FiscalDayCloseInitiated";

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Single(_admin.Closes);
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// A transport failure on generation is an ordinary failure. Generation reserves nothing and tells FDMS
    /// nothing, so treating it as an unknown outcome would park a day that never left our own network.
    /// </summary>
    [Fact]
    public async Task A_packaging_call_that_fails_in_transport_is_retried_rather_than_reconciled()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        _admin.OnGenerate = _ => throw new FiscalisationApiException(
            HttpStatusCode.ServiceUnavailable, null, "Service Unavailable", hasProblemDocument: false);

        var first = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(0, first.NeedsReconciliation);
        Assert.Equal(FiscalDayLifecycleStatus.Drained, (await ReadStateAsync()).Status);

        _admin.OnGenerate = null;

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// Every transition is written before the next device is touched. Held in memory to the end of the
    /// pass, a lost save discards a NeedsReconciliation — and the next pass re-sends a file that must never
    /// be sent twice.
    /// </summary>
    [Fact]
    public async Task A_days_transition_is_on_disk_before_the_next_device_is_touched()
    {
        const int SecondDeviceId = 43;

        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddSale("VAN007-1", DesktopSaleReceiptIngestStatus.Ingested, fiscalDeviceId: SecondDeviceId);
        await _context.SaveChangesAsync();

        using var cancellation = new CancellationTokenSource();

        // The second device's turn is where the pass dies — a host shutdown, mid-run.
        _deviceConfig.OnLookup = deviceId =>
        {
            if (deviceId == SecondDeviceId)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().AdvanceDueDaysAsync(NowUtc, cancellation.Token));

        // The first device finished and reached ZIMRA, and that fact survived the pass being torn down.
        Assert.Single(_admin.Submits);
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync(DeviceId)).Status);
    }

    /// <summary>
    /// Reading is how an unknown outcome is settled. FDMS reporting the day closed says the close did take,
    /// which is a fact no amount of retrying could have established.
    /// </summary>
    [Fact]
    public async Task An_unknown_close_is_settled_by_reading_the_device_status()
    {
        _deviceConfig.OperatingMode = "Online";
        AddSale("TILL-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.NeedsReconciliation);
        await _context.SaveChangesAsync();

        _platform.FiscalDayStatus = "FiscalDayClosed";

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Closes);
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(FiscalDayLifecycleStatus.Closed, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// FDMS listing the day's file as processed is the other half of the same idea: the upload did land, so
    /// the day is with ZIMRA and was all along.
    /// </summary>
    [Fact]
    public async Task An_unknown_upload_is_settled_by_reading_the_accepted_file_list()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.NeedsReconciliation, fileGenerated: true);
        await _context.SaveChangesAsync();

        _admin.OnFileStatus = _ => new GetFileStatusApiResponse
        {
            Total = 1,
            FileStatus =
            [
                new FileStatusDto
                {
                    OperationID = "op-from-fdms",
                    DeviceId = DeviceId,
                    FiscalDayNo = FiscalDayNo,
                    FileSequence = 1,
                    HasFooter = true,
                    FileProcessingStatus = "CompleteSuccessful"
                }
            ]
        };

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Submits);
        Assert.Equal(1, result.Reconciled);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, state.Status);
        Assert.Equal("op-from-fdms", state.OfflineFileReference);
    }

    /// <summary>
    /// FDMS having no record of the file is suggestive, not conclusive, and the only automated alternative
    /// to waiting is to upload something FDMS may already hold. It waits, and says why.
    /// </summary>
    [Fact]
    public async Task An_upload_FDMS_has_never_heard_of_is_left_for_a_person()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.NeedsReconciliation, fileGenerated: true);
        await _context.SaveChangesAsync();

        _admin.OnFileStatus = _ => new GetFileStatusApiResponse();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Submits);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, state.Status);
        Assert.Contains("lists no accepted file", state.LastError);
    }

    // ── Resuming ────────────────────────────────────────────────────────────

    /// <summary>
    /// A run that died after packaging picks up at the upload. It does not walk the day back through the
    /// drain, and it does not treat the generated file as lost.
    /// </summary>
    [Fact]
    public async Task A_day_left_at_FileGenerated_resumes_at_the_upload()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.FileGenerated, fileGenerated: true);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        // Packaging again is safe — it is a read on the platform's side, and it re-selects only receipts no
        // uploaded file already carries — so the resume asks for the bytes rather than having kept them.
        Assert.Single(_admin.Generates);
        Assert.Single(_admin.Submits);
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
        Assert.Equal(1, result.DaysSubmitted);
    }

    /// <summary>
    /// The day that most needs finishing is the one whose sales are long past the discovery window. Loading
    /// only recent sales would forget it, which is how a day stalls permanently and silently.
    /// </summary>
    [Fact]
    public async Task An_unfinished_day_is_resumed_even_when_its_sales_are_long_out_of_the_window()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested, docDate: NowUtc.Date.AddDays(-90));
        AddState(FiscalDayLifecycleStatus.FileGenerated, fileGenerated: true);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
    }

    /// <summary>A day already with ZIMRA is finished. Nothing looks at it again.</summary>
    [Fact]
    public async Task A_submitted_day_is_left_alone()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.Submitted, fileGenerated: true);
        await _context.SaveChangesAsync();

        await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Empty(_admin.Submits);
    }

    /// <summary>
    /// Today's day is not closed by a pass that happens to run mid-morning. The vans are still out, and the
    /// receipts they have not uploaded yet would be the ones stranded.
    /// </summary>
    [Fact]
    public async Task Todays_day_waits_for_the_configured_close_time()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested, openedAtLocal: NowLocal.AddMinutes(-30));
        await _context.SaveChangesAsync();

        // An hour from now, in the taxpayer's clock.
        var settings = Settings();
        settings.FiscalDay.CloseAtLocalTime = NowLocal.AddHours(1).ToString("HH:mm");

        await Service(settings).AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Equal(FiscalDayLifecycleStatus.Open, (await ReadStateAsync()).Status);
    }

    // ── Running out of time ─────────────────────────────────────────────────

    /// <summary>
    /// The lifecycle is walked far more often than a warning should be repeated. An alert that reappears
    /// every hour is one people learn to dismiss without reading, which costs the warning its only purpose.
    /// </summary>
    [Fact]
    public async Task The_duration_warning_is_raised_once_and_not_again()
    {
        _deviceConfig.TaxPayerDayMaxHrs = 24;

        // Twenty hours into a twenty-four hour limit, so past the 80% mark. Held open by an outstanding
        // receipt, which is what a day running out of time actually looks like.
        var opened = NowLocal.AddHours(-20);
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested, openedAtLocal: opened);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Pending, openedAtLocal: opened);
        await _context.SaveChangesAsync();

        var first = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(1, first.DurationWarnings);
        Assert.True((await ReadStateAsync()).DurationWarningRaised);

        var second = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(0, second.DurationWarnings);
    }

    /// <summary>
    /// The limit is the taxpayer's, read from the device, so a day well inside it is not warned about
    /// however long it has been running in absolute terms.
    /// </summary>
    [Fact]
    public async Task A_day_inside_the_taxpayers_limit_is_not_warned_about()
    {
        _deviceConfig.TaxPayerDayMaxHrs = 48;

        var opened = NowLocal.AddHours(-20);
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested, openedAtLocal: opened);
        AddSale("VAN006-2", DesktopSaleReceiptIngestStatus.Pending, openedAtLocal: opened);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(0, result.DurationWarnings);
        Assert.False((await ReadStateAsync()).DurationWarningRaised);
    }

    // ── Running out of attempts, and getting back in ────────────────────────

    /// <summary>
    /// Six refusals during a platform outage used to park a day permanently, and a parked day's receipts
    /// never reach ZIMRA at all. An outage is a condition that clears, so the budget pauses rather than
    /// ends: nothing is attempted while the pause runs, and the day is tried again afterwards.
    /// </summary>
    [Fact]
    public async Task A_day_that_used_up_its_attempts_is_paused_and_then_tried_again()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.Drained, attempts: 6, updatedAt: NowUtc.AddHours(-1));
        await _context.SaveChangesAsync();

        var paused = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Generates);
        Assert.Equal(1, paused.DaysAwaitingRetryBudget);
        Assert.Equal(0, paused.Failed);

        var pausedState = await ReadStateAsync();
        // Not Failed: nothing refused this day for a reason of its own, it ran out of attempts.
        Assert.Equal(FiscalDayLifecycleStatus.Drained, pausedState.Status);
        // Untouched, because the pause is measured from UpdatedAt and a pass that stamped it on the way
        // past would hold the day paused for as long as the passes keep coming.
        Assert.Equal(NowUtc.AddHours(-1), pausedState.UpdatedAt);

        var later = NowUtc.AddHours(13);
        var resumed = await Service().AdvanceDueDaysAsync(later, CancellationToken.None);

        Assert.Equal(0, resumed.DaysAwaitingRetryBudget);
        Assert.Single(_admin.Generates);
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
    }

    /// <summary>
    /// A day FDMS itself refused is the other thing entirely. Packaging and uploading again is the only
    /// automated response available and FDMS already holds the receipts the rejected file carried, so
    /// nothing automatic touches it — until a person has rebuilt it and says so.
    /// </summary>
    [Fact]
    public async Task A_day_FDMS_refused_waits_for_a_person_and_then_resumes_where_it_stopped()
    {
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.NeedsReconciliation, fileGenerated: true);
        await _context.SaveChangesAsync();

        _admin.OnFileStatus = _ => new GetFileStatusApiResponse
        {
            Total = 1,
            FileStatus =
            [
                new FileStatusDto
                {
                    OperationID = "op-rejected",
                    DeviceId = DeviceId,
                    FiscalDayNo = FiscalDayNo,
                    FileSequence = 1,
                    HasFooter = true,
                    FileProcessingStatus = "CompleteFailed",
                    FileProcessingErrorCode = "RCPT013"
                }
            ]
        };

        var refused = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Equal(1, refused.Failed);
        Assert.Equal(FiscalDayLifecycleStatus.Failed, (await ReadStateAsync()).Status);

        // No pass touches it again, whatever the platform now says.
        _admin.OnFileStatus = null;
        await Service().AdvanceDueDaysAsync(NowUtc.AddDays(2), CancellationToken.None);

        Assert.Empty(_admin.Submits);
        Assert.Equal(FiscalDayLifecycleStatus.Failed, (await ReadStateAsync()).Status);

        // A person rebuilds it on the platform and puts it back, and it resumes at the step it stopped on
        // rather than at the beginning.
        Assert.True(await Service().ResumeFailedDayAsync(
            DeviceId, FiscalDayNo, "tinashe", NowUtc.AddDays(2), CancellationToken.None));

        var reopened = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.FileGenerated, reopened.Status);
        Assert.Equal(0, reopened.Attempts);
        Assert.Contains("tinashe", reopened.LastError);

        await Service().AdvanceDueDaysAsync(NowUtc.AddDays(2), CancellationToken.None);

        Assert.Single(_admin.Submits);
        Assert.Equal(FiscalDayLifecycleStatus.Submitted, (await ReadStateAsync()).Status);
    }

    /// <summary>Only a refused day is resumable; nothing else has stopped, so nothing else is put back.</summary>
    [Fact]
    public async Task A_day_that_was_not_refused_is_not_resumable()
    {
        AddState(FiscalDayLifecycleStatus.NeedsReconciliation, fileGenerated: true);
        await _context.SaveChangesAsync();

        Assert.False(await Service().ResumeFailedDayAsync(
            DeviceId, FiscalDayNo, "tinashe", NowUtc, CancellationToken.None));
        Assert.False(await Service().ResumeFailedDayAsync(
            DeviceId, 999, "tinashe", NowUtc, CancellationToken.None));

        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, (await ReadStateAsync()).Status);
    }

    // ── Reading a device-level answer about one day ─────────────────────────

    /// <summary>
    /// The FDMS status read is device-level: after a close it describes the <em>next</em> day. Applied
    /// without comparing the day number, "FiscalDayOpened" about day 8 walks day 7 back to Drained and
    /// closes it a second time.
    /// </summary>
    [Fact]
    public async Task A_device_status_about_another_day_settles_nothing()
    {
        _deviceConfig.OperatingMode = "Online";
        AddSale("TILL-1", DesktopSaleReceiptIngestStatus.Ingested);
        AddState(FiscalDayLifecycleStatus.NeedsReconciliation);
        await _context.SaveChangesAsync();

        _platform.FiscalDayStatus = "FiscalDayOpened";
        _platform.ReportedFiscalDayNo = FiscalDayNo + 1;

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.Empty(_admin.Closes);
        Assert.Equal(0, result.Reconciled);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.NeedsReconciliation, state.Status);
        Assert.Contains("not day 7", state.LastError);
    }

    // ── Not configured ──────────────────────────────────────────────────────

    /// <summary>
    /// The platform authorises each call against the device it names and reads one FdmsDeviceId claim to do
    /// it, so one account cannot serve a fleet. Sending anyway spends six attempts on a bodyless 403 and
    /// leaves an operator reading the word "Forbidden".
    /// </summary>
    [Fact]
    public async Task A_device_with_no_credential_of_its_own_is_named_rather_than_left_to_a_403()
    {
        _admin.DevicesWithoutCredential.Add(DeviceId);
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.False(result.ServiceAccountMissing);
        Assert.Equal(1, result.DevicesWithoutServiceAccount);
        Assert.Empty(_admin.Generates);
        Assert.Empty(_admin.Submits);

        var state = await ReadStateAsync();
        Assert.Equal(FiscalDayLifecycleStatus.Open, state.Status);
        Assert.Contains("DeviceServiceAccounts__42", state.LastError);
    }


    /// <summary>
    /// Without a service account none of these routes can be reached at all. It is reported as a deployment
    /// step that has not happened rather than attempted once per device and failed.
    /// </summary>
    [Fact]
    public async Task Nothing_is_attempted_without_a_service_account()
    {
        _admin.IsConfigured = false;
        AddSale("VAN006-1", DesktopSaleReceiptIngestStatus.Ingested);
        await _context.SaveChangesAsync();

        var result = await Service().AdvanceDueDaysAsync(NowUtc, CancellationToken.None);

        Assert.True(result.ServiceAccountMissing);
        Assert.Empty(_admin.Generates);
        Assert.Empty(await _context.FiscalDayStates.ToListAsync());
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private FiscalDayLifecycleService Service(FiscalisationSettings? settings = null)
        => new(
            _context,
            _admin,
            _platform,
            _deviceConfig,
            Options.Create(settings ?? Settings()),
            NullLogger<FiscalDayLifecycleService>.Instance);

    /// <summary>Midnight, so that "has the close time passed" is true in every test that does not set it.</summary>
    private static FiscalisationSettings Settings() => new()
    {
        Enabled = true,
        BaseUrl = "https://fiscal.example/",
        ApiKey = "key",
        FiscalDay = new FiscalDaySettings
        {
            AutoCloseEnabled = true,
            CloseAtLocalTime = "00:00",
            WarnAtPercentOfMaxHrs = 80,
            ServiceAccount = new FiscalDayServiceAccountSettings { Username = "svc", Password = "pw" }
        }
    };

    private static FiscalisationApiException Indeterminate(string detail)
        => new(HttpStatusCode.Conflict, "FdmsOperationIndeterminate", detail);

    private Task<FiscalDayStateEntity> ReadStateAsync()
        => _context.FiscalDayStates
            .AsNoTracking()
            .SingleAsync(state => state.DeviceId == DeviceId && state.FiscalDayNo == FiscalDayNo);

    private void AddSale(
        string reference,
        DesktopSaleReceiptIngestStatus ingestStatus,
        DateTime? openedAtLocal = null,
        DateTime? docDate = null,
        int? fiscalDeviceId = DeviceId,
        string? fiscalDayNo = FiscalDayNoText,
        int? receiptGlobalNo = 500)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            DocDate = docDate ?? NowUtc.Date,
            TotalAmount = 100m,
            VatAmount = 13.04m,
            Currency = "USD",
            WarehouseCode = "VAN006",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDeviceId = fiscalDeviceId,
            FiscalDayNo = fiscalDayNo,
            FiscalDayOpenedAt = openedAtLocal ?? NowLocal.Date.AddHours(6),
            ReceiptGlobalNo = receiptGlobalNo,
            ReceiptCounter = 1,
            ReceiptIngestStatus = ingestStatus,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending
        });
    }

    private void AddState(
        FiscalDayLifecycleStatus status,
        bool fileGenerated = false,
        int deviceId = DeviceId,
        int attempts = 0,
        DateTime? updatedAt = null)
    {
        _context.FiscalDayStates.Add(new FiscalDayStateEntity
        {
            DeviceId = deviceId,
            FiscalDayNo = FiscalDayNo,
            OpenedAtLocal = NowLocal.Date.AddHours(6),
            Status = status,
            Attempts = attempts,
            FileGeneratedAtUtc = fileGenerated ? NowUtc.AddMinutes(-5) : null,
            OfflineFileReference = fileGenerated ? "1" : null,
            CreatedAt = updatedAt ?? NowUtc,
            // Stated rather than left to the entity's DateTime.UtcNow default: the attempt-budget pause is
            // measured from it, so a test about the pause cannot let it be the real clock.
            UpdatedAt = updatedAt ?? NowUtc
        });
    }

    private Task<FiscalDayStateEntity> ReadStateAsync(int deviceId)
        => _context.FiscalDayStates
            .AsNoTracking()
            .SingleAsync(state => state.DeviceId == deviceId && state.FiscalDayNo == FiscalDayNo);

    /// <summary>Records every admin call and answers with whatever the test installed.</summary>
    private sealed class RecordingAdminClient : IFiscalDayAdminApiClient
    {
        public bool IsConfigured { get; set; } = true;

        /// <summary>Devices this deployment has no credential for. Empty means every device is covered.</summary>
        public HashSet<int> DevicesWithoutCredential { get; } = [];

        public bool IsConfiguredForDevice(int deviceId)
            => IsConfigured && !DevicesWithoutCredential.Contains(deviceId);

        public List<OpenDayApiRequest> Opens { get; } = [];

        public List<CloseDayApiRequest> Closes { get; } = [];

        public List<GenerateOfflineFileApiRequest> Generates { get; } = [];

        public List<SubmitOfflineFileApiRequest> Submits { get; } = [];

        public List<int> FileStatusReads { get; } = [];

        public Func<CloseDayApiRequest, CloseDayApiResponse>? OnClose { get; set; }

        public Func<GenerateOfflineFileApiRequest, GenerateOfflineFileApiResponse>? OnGenerate { get; set; }

        public Func<SubmitOfflineFileApiRequest, SubmitOfflineFileApiResponse>? OnSubmit { get; set; }

        /// <summary>
        /// Raised instead of answering, for the failures that arrive as an exception from the transport
        /// rather than as a status the platform chose — a timeout, a reset, a gateway in front of it.
        /// </summary>
        public Func<SubmitOfflineFileApiRequest, Exception>? OnSubmitThrow { get; set; }

        public Func<CloseDayApiRequest, Exception>? OnCloseThrow { get; set; }

        public Func<int, GetFileStatusApiResponse>? OnFileStatus { get; set; }

        public Task<OpenDayApiResponse> OpenFiscalDayAsync(
            OpenDayApiRequest request, CancellationToken cancellationToken = default)
        {
            Opens.Add(request);
            return Task.FromResult(new OpenDayApiResponse { OperationID = "op-open", FiscalDayNo = request.FiscalDayNo ?? 1 });
        }

        public Task<CloseDayApiResponse> CloseFiscalDayAsync(
            CloseDayApiRequest request, CancellationToken cancellationToken = default)
        {
            Closes.Add(request);

            if (OnCloseThrow is not null)
            {
                throw OnCloseThrow(request);
            }

            return Task.FromResult(OnClose is null
                ? new CloseDayApiResponse { OperationID = "op-close" }
                : OnClose(request));
        }

        public Task<GenerateOfflineFileApiResponse> GenerateOfflineFileAsync(
            GenerateOfflineFileApiRequest request, CancellationToken cancellationToken = default)
        {
            Generates.Add(request);

            return Task.FromResult(OnGenerate?.Invoke(request) ?? new GenerateOfflineFileApiResponse
            {
                DeviceId = request.DeviceId,
                FiscalDayNo = request.FiscalDayNo,
                Files =
                [
                    new OfflineFilePackageDto
                    {
                        FileSequence = 1,
                        FileName = "fdms-offline.json",
                        FileJson = "{}",
                        ReceiptCount = 1,
                        DeclaresFiscalDayClose = request.CloseFiscalDay
                    }
                ]
            });
        }

        public Task<SubmitOfflineFileApiResponse> SubmitOfflineFileAsync(
            SubmitOfflineFileApiRequest request, CancellationToken cancellationToken = default)
        {
            Submits.Add(request);

            if (OnSubmitThrow is not null)
            {
                throw OnSubmitThrow(request);
            }

            return Task.FromResult(OnSubmit?.Invoke(request) ?? new SubmitOfflineFileApiResponse
            {
                OperationID = "op-settled",
                FiscalDayNo = request.FiscalDayNo ?? 0,
                ReceiptCount = 1,
                StampedReceiptCount = 1,
                DeclaredFiscalDayClose = true,
                Status = "Reconciled",
                ReconciliationRequired = false
            });
        }

        public Task<GetFileStatusApiResponse> GetOfflineFileStatusAsync(
            int deviceId,
            DateTime fileUploadedFrom,
            DateTime fileUploadedTill,
            CancellationToken cancellationToken = default)
        {
            FileStatusReads.Add(deviceId);
            return Task.FromResult(OnFileStatus?.Invoke(deviceId) ?? new GetFileStatusApiResponse());
        }
    }

    private sealed class StubDeviceConfigCache : IFiscalDeviceConfigCache
    {
        public string OperatingMode { get; set; } = "Offline";

        public int TaxPayerDayMaxHrs { get; set; } = 24;

        /// <summary>
        /// Runs when a device's configuration is looked up, which is the first thing the per-device step
        /// does. A test uses it to make something happen <em>during</em> the pass — a receipt landing, or
        /// the host being told to stop — rather than only before it.
        /// </summary>
        public Action<int>? OnLookup { get; set; }

        public Task<FiscalConfigApiResponse?> TryGetAsync(int deviceId, CancellationToken cancellationToken = default)
        {
            OnLookup?.Invoke(deviceId);

            return Task.FromResult<FiscalConfigApiResponse?>(new FiscalConfigApiResponse
            {
                DeviceSerialNo = $"ZIM-{deviceId}",
                DeviceOperatingMode = OperatingMode,
                TaxPayerDayMaxHrs = TaxPayerDayMaxHrs
            });
        }
    }

    /// <summary>
    /// Only the device-status read is reachable from the lifecycle; everything else on this client belongs
    /// to the receipt paths and would be a bug to call from here.
    /// </summary>
    private sealed class StubFiscalisationClient : IFiscalisationApiClient
    {
        public string FiscalDayStatus { get; set; } = "FiscalDayOpened";

        /// <summary>
        /// Which day the device-level status is about. Defaults to the day under test, because the read is
        /// only evidence about a day when it names that day.
        /// </summary>
        public int ReportedFiscalDayNo { get; set; } = FiscalDayNo;

        public Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new FiscalStatusApiResponse
            {
                DeviceId = deviceId,
                FiscalDayNo = ReportedFiscalDayNo,
                FiscalDayStatus = FiscalDayStatus
            });

        public Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(
            SapFiscaliseReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubmitReceiptApiResponse> SubmitReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreflightReceiptApiResponse> PreflightReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreflightReceiptApiResponse> PreflightSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
            int deviceId, string invoiceNo, ReceiptType receiptType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FiscalConfigApiResponse> GetFiscalConfigWithApiKeyAsync(
            string? apiKey, int deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
