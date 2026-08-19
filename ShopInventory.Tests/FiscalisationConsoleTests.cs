using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.FiscalisationConfiguration.Queries;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalDayStates;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationConsoleDevices;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationWorkQueue;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;
using ShopInventory.Web.Components;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The fiscalisation console's two load-bearing decisions: what counts as outstanding, and what may be
/// sent to ZIMRA a second time.
///
/// The second one is why these tests exist at all. A fiscal receipt cannot be withdrawn, so classifying
/// a platform response wrongly in either direction is expensive and silent: read "already fiscalised" as
/// a failure and every completed invoice raises a fresh incident forever, read an indeterminate outcome
/// as a failure and the obvious next step — press it again — signs the same sale twice. Neither is
/// reproducible against the live platform without causing it.
/// </summary>
public sealed class FiscalisationConsoleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FiscalisationConsoleTests()
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

    // ── What may be retried ─────────────────────────────────────────────────

    [Fact]
    public void AlreadyFiscalised_is_a_success_not_a_failure()
    {
        // The platform answers this as a 400. Taken at face value it is a permanent red row on a
        // document that is already compliant.
        var result = FiscalRetryResult.From(40312, new FiscalizationResult
        {
            Success = false,
            ErrorCode = "AlreadyFiscalised",
            Message = "Already fiscalised as receipt 88104 on device 1."
        });

        Assert.Equal(FiscalRetryOutcome.AlreadyFiscalised, result.Outcome);
        Assert.True(result.IsSettled);
        Assert.False(result.MustNotRetry);
    }

    [Fact]
    public void An_indeterminate_outcome_must_not_be_retried()
    {
        var result = FiscalRetryResult.From(40312, new FiscalizationResult
        {
            Success = false,
            RequiresReconciliation = true,
            ErrorCode = "FdmsOperationIndeterminate"
        });

        Assert.Equal(FiscalRetryOutcome.Reconcile, result.Outcome);
        Assert.True(result.MustNotRetry);
        Assert.False(result.IsSettled);
    }

    [Theory]
    [InlineData("IDEMPOTENCY_CONFLICT")]
    [InlineData("IDEMPOTENCY_KEY_REQUIRED")]
    // Deliberately a code no one has written yet: the family is matched by prefix precisely so that a
    // new conflict the platform invents defaults to "do not retry" rather than to "press it again".
    [InlineData("IDEMPOTENCY_SOMETHING_NEW")]
    [InlineData("ChainBreak")]
    public void Every_idempotency_conflict_is_a_reconcile(string errorCode)
    {
        var result = FiscalRetryResult.From(40312, new FiscalizationResult
        {
            Success = false,
            ErrorCode = errorCode
        });

        Assert.Equal(FiscalRetryOutcome.Reconcile, result.Outcome);
        Assert.True(result.MustNotRetry);
    }

    [Fact]
    public void A_plain_refusal_stays_retryable()
    {
        var result = FiscalRetryResult.From(40312, new FiscalizationResult
        {
            Success = false,
            ErrorCode = "FISCALISATION_ERROR",
            Message = "The platform timed out before the request reached FDMS."
        });

        Assert.Equal(FiscalRetryOutcome.Failed, result.Outcome);
        Assert.False(result.MustNotRetry);
        Assert.False(result.IsSettled);
    }

    [Fact]
    public void A_skipped_submission_is_not_reported_as_fiscalised()
    {
        // Dry run: the platform mapped and logged the receipt and sent nothing to FDMS. Success is
        // false and Skipped is true, and the pair must not be read as "already done".
        var result = FiscalRetryResult.From(40312, new FiscalizationResult
        {
            Success = false,
            Skipped = true,
            ErrorCode = "DryRun"
        });

        Assert.Equal(FiscalRetryOutcome.Skipped, result.Outcome);
        Assert.False(result.IsSettled);
    }

    [Fact]
    public void A_fresh_success_is_fiscalised()
    {
        var result = FiscalRetryResult.From(40312, new FiscalizationResult
        {
            Success = true,
            ReceiptGlobalNo = "88214"
        });

        Assert.Equal(FiscalRetryOutcome.Fiscalised, result.Outcome);
        Assert.True(result.IsSettled);
    }

    [Fact]
    public void An_outcome_nobody_established_must_not_be_retried()
    {
        var result = FiscalRetryResult.Unknown(40312, "The upstream request timed out.");

        Assert.Equal(FiscalRetryOutcome.Unknown, result.Outcome);
        Assert.True(result.MustNotRetry);
        Assert.False(result.IsSettled);
    }

    [Fact]
    public async Task A_submission_that_threw_is_reported_as_unknown_not_as_nothing_sent()
    {
        // InvoiceService throws on every non-2xx and on every transport fault alike, so a read timeout
        // on a submission that did reach FDMS arrives here indistinguishable from one that never left.
        // Reporting it as "nothing was sent" invited the click that signs the same sale twice.
        var invoices = new ThrowingInvoiceService(new HttpRequestException(
            "The fiscalisation platform did not answer within 30 seconds.",
            null,
            HttpStatusCode.GatewayTimeout));

        var console = new FiscalisationConsoleService(
            new HttpClient { BaseAddress = new Uri("http://localhost") },
            invoices,
            NullLogger<FiscalisationConsoleService>.Instance);

        var result = await console.RetryInvoiceAsync(40312);

        Assert.Equal(FiscalRetryOutcome.Unknown, result.Outcome);
        Assert.True(result.MustNotRetry);
        Assert.DoesNotContain("Nothing was sent", result.Message, StringComparison.OrdinalIgnoreCase);

        // The API's own wording survives. It is the only thing that says where to look.
        Assert.Contains("did not answer within 30 seconds", result.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_that_was_never_looked_up_is_still_safe_to_try_again()
    {
        // The other side of the rule above. Nothing left this application, so nothing may have reached
        // FDMS, and locking the row out would strand a document that only needs SAP to answer.
        var console = new FiscalisationConsoleService(
            new HttpClient { BaseAddress = new Uri("http://localhost") },
            new MissingInvoiceService(),
            NullLogger<FiscalisationConsoleService>.Instance);

        var result = await console.RetryInvoiceAsync(40312);

        Assert.Equal(FiscalRetryOutcome.Blocked, result.Outcome);
        Assert.False(result.MustNotRetry);
    }

    // ── What counts as outstanding ──────────────────────────────────────────

    [Fact]
    public async Task A_skipped_sale_is_not_owed_to_anyone()
    {
        // Fiscalisation was off when it was made, so nothing is owed. Counting it would put a floor
        // under a queue whose whole value is reaching zero.
        await SeedSaleAsync("SKIPPED-1", fiscal: DesktopSaleFiscalizationStatus.Skipped);
        await SeedSaleAsync("PENDING-1", fiscal: DesktopSaleFiscalizationStatus.Pending);

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("PENDING-1", Assert.Single(result.Items).Reference);
    }

    [Fact]
    public async Task A_sale_behind_on_both_steps_appears_once_under_the_worse_one()
    {
        await SeedSaleAsync(
            "BOTH-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            ingest: DesktopSaleReceiptIngestStatus.ChainBroken,
            ingestError: "ChainBreak: previous receipt hash does not match.");

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        var item = Assert.Single(result.Items);
        Assert.Equal("Chain broken", item.Status);
        Assert.Equal("Hand-over", item.Stage);
        Assert.Equal(FiscalWorkQueueDispositions.Unrecoverable, item.Disposition);
    }

    [Fact]
    public async Task An_unresolved_sale_is_never_offered_a_retry()
    {
        await SeedSaleAsync(
            "UNRESOLVED-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            requiresReconciliation: true);

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        var item = Assert.Single(result.Items);
        Assert.Equal(FiscalWorkQueueDispositions.Reconcile, item.Disposition);
        Assert.Equal("bad", item.Severity);
    }

    [Fact]
    public async Task An_unstamped_sale_is_reported_and_not_treated_as_a_chain_hole()
    {
        // The pair the van upload actually writes, which is the whole point of this test: an unstamped
        // sale is Failed *and* Unstamped on the same row, so it reaches the fiscalisation-failure arm
        // first unless the unstamped arm is ordered ahead of it. It used to, and every one of these read
        // as "fiscalisation failed — the background sweep retries this": false twice over, because
        // nothing was ever stamped and the drain skips unstamped rows deliberately.
        await SeedSaleAsync(
            "UNSTAMPED-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            ingest: DesktopSaleReceiptIngestStatus.Unstamped);

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        var item = Assert.Single(result.Items);
        Assert.Equal("Never stamped", item.Status);
        Assert.Equal(FiscalWorkQueueDispositions.Unrecoverable, item.Disposition);
        Assert.DoesNotContain("retries", item.DispositionNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sweep", item.DispositionNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unstamped_sale_is_still_selected_by_its_own_filter()
    {
        // The same row from the other direction. Reordering the arms must not move it out of the filter
        // whose whole job is to list the sales nobody can produce a receipt for.
        await SeedSaleAsync(
            "UNSTAMPED-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            ingest: DesktopSaleReceiptIngestStatus.Unstamped);

        var result = await RunQueueAsync(
            new GetFiscalisationWorkQueueQuery(Status: FiscalWorkQueueFilters.Unstamped));

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Never stamped", Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task The_status_filter_narrows_the_count_not_just_the_page()
    {
        // The point of the whole query. Filtering after the fetch — what the invoices page does — leaves
        // the total describing something other than the list under it.
        await SeedSaleAsync("CHAIN-1", ingest: DesktopSaleReceiptIngestStatus.ChainBroken);
        await SeedSaleAsync("CHAIN-2", ingest: DesktopSaleReceiptIngestStatus.ChainBroken);
        await SeedSaleAsync("PENDING-1", fiscal: DesktopSaleFiscalizationStatus.Pending);
        await SeedSaleAsync("PENDING-2", fiscal: DesktopSaleFiscalizationStatus.Pending);

        var all = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());
        var chainBroken = await RunQueueAsync(
            new GetFiscalisationWorkQueueQuery(Status: FiscalWorkQueueFilters.ChainBroken));

        Assert.Equal(4, all.TotalCount);
        Assert.Equal(2, chainBroken.TotalCount);
        Assert.All(chainBroken.Items, item => Assert.Equal("Chain broken", item.Status));
    }

    [Fact]
    public async Task A_device_filter_never_matches_a_SAP_document()
    {
        // The platform picks the device for a SAP submission, and may fail over mid-flight. Nothing
        // records which it chose, so a document cannot honestly be attributed to one.
        await SeedSaleAsync("VAN-1", ingest: DesktopSaleReceiptIngestStatus.ChainBroken, deviceId: 3);
        await SeedDocumentAsync(40312, "Not Fiscalised");

        var unfiltered = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());
        var onDevice = await RunQueueAsync(new GetFiscalisationWorkQueueQuery(DeviceId: 3));

        Assert.Equal(2, unfiltered.TotalCount);
        Assert.Equal(1, onDevice.TotalCount);
        Assert.Equal(0, onDevice.DocumentCount);
    }

    [Fact]
    public async Task A_document_this_console_fiscalised_leaves_the_queue()
    {
        // Two rows for one document. Only the newest describes it, and a document once fiscalised does
        // not go back — so an old "not fiscalised" row must not keep it in the queue forever.
        //
        // "Success", not "Fiscalised", because that is what the manual fiscalise path writes and this
        // page is the thing that calls it. Testing the exclusion against a status no production writer
        // produces is how the queue came to keep every document the console had just fiscalised, still
        // offering a Fiscalise button on it, with a count that could never reach zero.
        await SeedDocumentAsync(40312, "Not Fiscalised", syncedAt: new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc));
        await SeedDocumentAsync(40312, "Success", syncedAt: new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc));

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task A_document_holding_a_receipt_number_leaves_the_queue_whatever_its_status_says()
    {
        // Evidence beats status, which is the rule the rest of the codebase reads by
        // (FiscalDocumentStatusProjector) and now the only rule here too. A row carrying a receipt
        // number, a QR code or a verification code describes a document ZIMRA has, however the status
        // column was left.
        await SeedDocumentAsync(40312, "Failed", receiptGlobalNo: 88104);

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task An_unresolved_document_is_not_offered_a_retry_after_a_reload()
    {
        // The verdict has to survive the page being refreshed. A SAP document has no reconciliation
        // column and the manual path writes an indeterminate outcome as a plain "Failed", so the only
        // surviving trace is the wording recorded with it — and the console's in-session lock-out is
        // gone the moment someone presses F5.
        await SeedDocumentAsync(
            40312,
            "Failed",
            message: "The fiscal outcome is unresolved. Check the receipt on the fiscalisation console "
                + "before any resubmission — it may already exist.");

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal("Unresolved", item.Status);
        Assert.Equal("bad", item.Severity);
        Assert.Equal(FiscalWorkQueueDispositions.Reconcile, item.Disposition);
    }

    [Fact]
    public async Task A_document_refused_for_a_stated_reason_is_still_retryable()
    {
        // The other side of the rule above. Reading every failure as ambiguous would take the one action
        // this page exists to offer away from the documents that need it.
        await SeedDocumentAsync(
            40312,
            "Failed",
            message: "RCPT025: line 2 uses tax id 3, which this device does not have.");

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal(FiscalWorkQueueDispositions.Retry, item.Disposition);
    }

    [Fact]
    public async Task A_SAP_invoice_is_the_only_thing_this_page_may_send()
    {
        await SeedDocumentAsync(40312, "Not Fiscalised");
        await SeedDocumentAsync(9001, "Not Fiscalised", documentType: "CreditNote");

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery());

        var invoice = Assert.Single(result.Items, item => item.Source == "SAP invoice");
        var creditNote = Assert.Single(result.Items, item => item.Source == "SAP credit note");

        Assert.Equal(FiscalWorkQueueDispositions.Retry, invoice.Disposition);
        Assert.Equal(FiscalWorkQueueDispositions.Automatic, creditNote.Disposition);
    }

    // ── Who is going to fix it ──────────────────────────────────────────────

    [Fact]
    public async Task A_failed_vending_sale_inside_the_sweeps_reach_is_handled_automatically()
    {
        await SeedSaleAsync(
            "VEND-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            sourceSystem: SaleSourceSystems.Vending);

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal(FiscalWorkQueueDispositions.Automatic, item.Disposition);
    }

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.VanSales)]
    public async Task A_failed_sale_the_sweep_does_not_read_is_not_called_automatic(string sourceSystem)
    {
        // DesktopSaleFiscalisationSweep selects vending only. Telling a shop-till or van sale it is
        // "handled automatically" names an owner that does not exist, and the row then sits in the queue
        // being ignored by the operator as well as by every scheduled run.
        await SeedSaleAsync(
            "STRANDED-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            sourceSystem: sourceSystem);

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal(FiscalWorkQueueDispositions.Stalled, item.Disposition);
        Assert.Contains("vending", item.DispositionNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_vending_sale_older_than_the_lookback_is_not_called_automatic()
    {
        await SeedSaleAsync(
            "OLD-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            sourceSystem: SaleSourceSystems.Vending,
            docDate: DateTime.UtcNow.Date.AddDays(-(SweepSettings.LookbackDays + 1)));

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal(FiscalWorkQueueDispositions.Stalled, item.Disposition);
        Assert.Contains(SweepSettings.LookbackDays.ToString(), item.DispositionNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vending_sale_out_of_attempts_is_not_called_automatic()
    {
        // The attempt budget is a hard stop, not a slowdown. A sale that has spent it is waiting for a
        // person whether or not anyone has told them.
        await SeedSaleAsync(
            "SPENT-1",
            fiscal: DesktopSaleFiscalizationStatus.Failed,
            sourceSystem: SaleSourceSystems.Vending,
            fiscalAttempts: SweepSettings.MaxFiscalisationAttempts);

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal(FiscalWorkQueueDispositions.Stalled, item.Disposition);
    }

    [Fact]
    public async Task A_pending_shop_till_sale_is_not_called_automatic_either()
    {
        // Nothing sweeps Pending outside vending. A till sale stuck here is a request that never
        // finished, and no scheduled run will finish it.
        await SeedSaleAsync(
            "TILL-1",
            fiscal: DesktopSaleFiscalizationStatus.Pending,
            sourceSystem: SaleSourceSystems.ShopTill);

        var item = Assert.Single((await RunQueueAsync(new GetFiscalisationWorkQueueQuery())).Items);

        Assert.Equal("Awaiting fiscalisation", item.Status);
        Assert.Equal(FiscalWorkQueueDispositions.Stalled, item.Disposition);
    }

    // ── Colour, and paging ──────────────────────────────────────────────────

    [Theory]
    [InlineData(FiscalWorkQueueFilters.AwaitingFiscalisation)]
    [InlineData(FiscalWorkQueueFilters.FiscalisationFailed)]
    [InlineData(FiscalWorkQueueFilters.NeedsReconciliation)]
    [InlineData(FiscalWorkQueueFilters.HandoverFailed)]
    [InlineData(FiscalWorkQueueFilters.ChainBroken)]
    [InlineData(FiscalWorkQueueFilters.Unsignable)]
    [InlineData(FiscalWorkQueueFilters.Unstamped)]
    public async Task A_filters_swatch_is_the_colour_of_the_rows_it_selects(string filter)
    {
        // The console's filter menu paints each entry with FiscalWorkQueueFilters.SeverityOf. If a row
        // selected by that filter came back a different family, the colour someone filtered by would not
        // be the colour they then read — and on this page the colour is the summary they act on.
        await SeedForFilterAsync(filter);

        var result = await RunQueueAsync(new GetFiscalisationWorkQueueQuery(Status: filter));

        var item = Assert.Single(result.Items);
        Assert.Equal(FiscalWorkQueueFilters.SeverityOf(filter), item.Severity);
    }

    [Fact]
    public void Every_work_queue_filter_in_the_menu_is_the_colour_of_the_rows_it_selects()
    {
        Assert.All(
            FiscalConsoleMenus.WorkQueueStatus.Where(option => !string.IsNullOrEmpty(option.Value)),
            option => Assert.Equal(FiscalWorkQueueFilter.SeverityOf(option.Value), option.Family));
    }

    [Fact]
    public void Every_lifecycle_filter_in_the_menu_is_the_colour_of_the_rows_it_selects()
    {
        // This is the pairing that had actually come apart: the menu painted an open day "info" and a
        // closed one "accent" while the table drew both grey, so the swatch someone filtered by was not
        // the dot they then read.
        Assert.All(
            FiscalConsoleMenus.FiscalDayStatus.Where(option =>
                FiscalDayStatusFilter.Lifecycle.Contains(option.Value)),
            option => Assert.Equal(FiscalConsoleMenus.DaySeverity(option.Value), option.Family));

        // Every step of the lifecycle is offered, or the filter silently cannot reach one of them.
        Assert.All(
            FiscalDayStatusFilter.Lifecycle,
            status => Assert.Contains(FiscalConsoleMenus.FiscalDayStatus, option => option.Value == status));
    }

    [Fact]
    public void The_web_filter_menu_agrees_with_the_api_about_every_colour()
    {
        // Two constants classes, one on each side of the wire, hand-mirrored like every other DTO here.
        // This is the test that stops them drifting.
        string[] filters =
        [
            FiscalWorkQueueFilters.All,
            FiscalWorkQueueFilters.AwaitingFiscalisation,
            FiscalWorkQueueFilters.FiscalisationFailed,
            FiscalWorkQueueFilters.NeedsReconciliation,
            FiscalWorkQueueFilters.HandoverFailed,
            FiscalWorkQueueFilters.ChainBroken,
            FiscalWorkQueueFilters.Unsignable,
            FiscalWorkQueueFilters.Unstamped
        ];

        Assert.All(filters, filter => Assert.Equal(
            FiscalWorkQueueFilters.SeverityOf(filter),
            FiscalWorkQueueFilter.SeverityOf(filter)));
    }

    [Fact]
    public async Task A_page_past_the_end_of_the_queue_is_clamped_rather_than_overflowing()
    {
        // page * pageSize was only floored, never capped. At a large page it overflows to a negative
        // Take and at a merely large one it reads that many rows out of each of the two sources.
        await SeedSaleAsync("PENDING-1", fiscal: DesktopSaleFiscalizationStatus.Pending);

        var result = await RunQueueAsync(
            new GetFiscalisationWorkQueueQuery(Page: int.MaxValue, PageSize: FiscalConsolePaging.MaxPageSize));

        Assert.Equal(FiscalConsolePaging.MaxReach / FiscalConsolePaging.MaxPageSize, result.Page);
        Assert.Empty(result.Items);
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task A_page_past_the_end_of_the_fiscal_days_is_clamped_rather_than_overflowing()
    {
        await SeedFiscalDayAsync(3, 210, FiscalDayLifecycleStatus.Open);

        var result = await RunDaysAsync(
            new GetFiscalDayStatesQuery(Page: int.MaxValue, PageSize: FiscalConsolePaging.MaxPageSize));

        Assert.Equal(FiscalConsolePaging.MaxReach / FiscalConsolePaging.MaxPageSize, result.Page);
        Assert.Empty(result.Days);
    }

    // ── Devices ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_broken_chain_names_the_first_break_and_counts_what_is_stuck_behind_it()
    {
        // Only the first break is a fault; the ones after it are consequences of it. Reporting each as
        // its own would turn one stopped van into a screen of them.
        await SeedSaleAsync("BREAK-1", ingest: DesktopSaleReceiptIngestStatus.ChainBroken,
            deviceId: 3, receiptGlobalNo: 4471, ingestError: "ChainBreak: hash mismatch at 4470.");
        await SeedSaleAsync("BREAK-2", ingest: DesktopSaleReceiptIngestStatus.ChainBroken,
            deviceId: 3, receiptGlobalNo: 4478);
        await SeedSaleAsync("BEHIND-1", ingest: DesktopSaleReceiptIngestStatus.Pending,
            deviceId: 3, receiptGlobalNo: 4472);
        await SeedSaleAsync("BEHIND-2", ingest: DesktopSaleReceiptIngestStatus.Pending,
            deviceId: 3, receiptGlobalNo: 4473);
        await SeedSaleAsync("EARLIER", ingest: DesktopSaleReceiptIngestStatus.Pending,
            deviceId: 3, receiptGlobalNo: 4460);

        var device = Assert.Single(await RunDevicesAsync());

        Assert.True(device.ChainBroken);
        Assert.Equal(4471, device.ChainBrokenAtReceiptGlobalNo);
        Assert.Equal("ChainBreak: hash mismatch at 4470.", device.ChainBrokenError);
        // 4472 and 4473 only. 4460 was signed before the break and is not held by it.
        Assert.Equal(2, device.BlockedBehindChainBreak);
        Assert.Equal(3, device.AwaitingHandover);
    }

    [Fact]
    public async Task A_device_the_platform_will_not_answer_for_is_still_listed()
    {
        // Dropping it would make an outage look like a shorter fleet, which is the failure this page
        // exists to prevent.
        await SeedSaleAsync("PENDING-1", ingest: DesktopSaleReceiptIngestStatus.Pending, deviceId: 9);

        var device = Assert.Single(await RunDevicesAsync(new SilentFiscalisationClient()));

        Assert.Equal(9, device.DeviceId);
        Assert.False(device.Reachable);
        Assert.Contains("DeviceNotFound", device.PlatformError);
        Assert.Equal(1, device.AwaitingHandover);
    }

    [Fact]
    public async Task A_fiscal_day_is_measured_against_the_taxpayers_own_limit()
    {
        await SeedSaleAsync("PENDING-1", ingest: DesktopSaleReceiptIngestStatus.Pending, deviceId: 1);

        var openedHoursAgo = 18;
        var client = new StubFiscalisationClient
        {
            MaxHours = 24,
            FiscalDayOpened = ShopInventory.Services.AuditService
                .ToCAT(DateTime.UtcNow)
                .AddHours(-openedHoursAgo)
        };

        var device = Assert.Single(await RunDevicesAsync(client));

        Assert.NotNull(device.FiscalDayHoursElapsed);
        Assert.InRange(device.FiscalDayHoursElapsed!.Value, openedHoursAgo - 0.2, openedHoursAgo + 0.2);
        Assert.Equal(75, device.FiscalDayPercentOfMax);
    }

    // ── Fiscal days ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Only_a_submitted_day_counts_as_finished()
    {
        // A closed day, and even a day whose file exists, is still a day ZIMRA has nothing for.
        await SeedFiscalDayAsync(3, 210, FiscalDayLifecycleStatus.Submitted);
        await SeedFiscalDayAsync(3, 211, FiscalDayLifecycleStatus.FileGenerated);
        await SeedFiscalDayAsync(3, 212, FiscalDayLifecycleStatus.NeedsReconciliation);

        var result = await RunDaysAsync(new GetFiscalDayStatesQuery());

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.OutstandingCount);
        Assert.Equal(1, result.NeedsAttentionCount);
    }

    [Fact]
    public async Task An_unrecognised_lifecycle_filter_is_refused_rather_than_ignored()
    {
        // A filter that silently does nothing shows the full list under a heading claiming it is
        // narrowed, which is exactly the mistake this page exists to stop.
        await SeedFiscalDayAsync(3, 210, FiscalDayLifecycleStatus.Open);

        var result = await new GetFiscalDayStatesHandler(_context).Handle(
            new GetFiscalDayStatesQuery(Status: "almost-done"),
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Needs_attention_selects_both_of_the_stopped_states()
    {
        await SeedFiscalDayAsync(1, 400, FiscalDayLifecycleStatus.Open);
        await SeedFiscalDayAsync(1, 401, FiscalDayLifecycleStatus.Failed);
        await SeedFiscalDayAsync(1, 402, FiscalDayLifecycleStatus.NeedsReconciliation);

        var result = await RunDaysAsync(
            new GetFiscalDayStatesQuery(Status: GetFiscalDayStatesHandler.NeedsAttentionFilter));

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Days, day => Assert.True(day.NeedsAttention));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>
    /// The sweep's own configuration, because the queue's "is anything going to pick this up" answer is
    /// measured against it.
    /// </summary>
    private static readonly DesktopSalePostingSettings SweepSettings = new()
    {
        LookbackDays = 3,
        MaxFiscalisationAttempts = 5
    };

    private async Task<FiscalWorkQueueResult> RunQueueAsync(GetFiscalisationWorkQueueQuery query)
    {
        var handler = new GetFiscalisationWorkQueueHandler(
            _context,
            Options.Create(SweepSettings));

        var result = await handler.Handle(query, CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private async Task<FiscalDayStateListResult> RunDaysAsync(GetFiscalDayStatesQuery query)
    {
        var result = await new GetFiscalDayStatesHandler(_context).Handle(query, CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private async Task<List<FiscalConsoleDeviceDto>> RunDevicesAsync(IFiscalisationApiClient? client = null)
    {
        var handler = new GetFiscalisationConsoleDevicesHandler(
            _context,
            client ?? new StubFiscalisationClient(),
            new StaticOptionsMonitor<FiscalisationSettings>(new FiscalisationSettings
            {
                Enabled = true,
                ApiKey = "test-key"
            }),
            NullLogger<GetFiscalisationConsoleDevicesHandler>.Instance);

        var result = await handler.Handle(new GetFiscalisationConsoleDevicesQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    /// <summary>
    /// A sale dated today by default, because whether the sweep still reaches it is measured from the
    /// clock rather than from a fixed date in the fixture.
    /// </summary>
    private async Task SeedSaleAsync(
        string reference,
        DesktopSaleFiscalizationStatus fiscal = DesktopSaleFiscalizationStatus.Success,
        DesktopSaleReceiptIngestStatus ingest = DesktopSaleReceiptIngestStatus.NotApplicable,
        bool requiresReconciliation = false,
        string? ingestError = null,
        int? deviceId = null,
        int? receiptGlobalNo = null,
        string sourceSystem = SaleSourceSystems.VanSales,
        DateTime? docDate = null,
        int fiscalAttempts = 0)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = sourceSystem,
            CardCode = "VAN07",
            CardName = "Van 07",
            WarehouseCode = "VAN07",
            Currency = "ZWG",
            DocDate = docDate ?? DateTime.UtcNow.Date,
            CreatedAt = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            TotalAmount = 100m,
            FiscalizationStatus = fiscal,
            FiscalizationRequiresReconciliation = requiresReconciliation,
            FiscalizationAttempts = fiscalAttempts,
            ReceiptIngestStatus = ingest,
            ReceiptIngestError = ingestError,
            FiscalDeviceId = deviceId,
            ReceiptGlobalNo = receiptGlobalNo
        });

        await _context.SaveChangesAsync();
    }

    private async Task SeedDocumentAsync(
        int docNum,
        string status,
        string documentType = "Invoice",
        DateTime? syncedAt = null,
        string? message = null,
        int? receiptGlobalNo = null)
    {
        var stamp = syncedAt ?? new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc);

        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = $"{documentType}-{docNum}-{stamp.Ticks}",
            DocumentType = documentType,
            DocNum = docNum,
            Status = status,
            Message = message,
            ReceiptGlobalNo = receiptGlobalNo,
            CardName = "Kefalos Wholesale",
            DocTotal = 12480m,
            Currency = "USD",
            TimestampUtc = stamp,
            LastSyncedAtUtc = stamp
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>One sale that the named filter selects, in the state the writers actually record.</summary>
    private Task SeedForFilterAsync(string filter) => filter switch
    {
        FiscalWorkQueueFilters.AwaitingFiscalisation =>
            SeedSaleAsync("F-1", fiscal: DesktopSaleFiscalizationStatus.Pending),

        FiscalWorkQueueFilters.FiscalisationFailed =>
            SeedSaleAsync("F-1", fiscal: DesktopSaleFiscalizationStatus.Failed),

        FiscalWorkQueueFilters.NeedsReconciliation =>
            SeedSaleAsync("F-1", fiscal: DesktopSaleFiscalizationStatus.Failed, requiresReconciliation: true),

        FiscalWorkQueueFilters.HandoverFailed =>
            SeedSaleAsync("F-1", ingest: DesktopSaleReceiptIngestStatus.Failed),

        FiscalWorkQueueFilters.ChainBroken =>
            SeedSaleAsync("F-1", ingest: DesktopSaleReceiptIngestStatus.ChainBroken),

        FiscalWorkQueueFilters.Unsignable =>
            SeedSaleAsync("F-1", ingest: DesktopSaleReceiptIngestStatus.Unsignable),

        // Both halves, as the van upload writes them.
        FiscalWorkQueueFilters.Unstamped =>
            SeedSaleAsync(
                "F-1",
                fiscal: DesktopSaleFiscalizationStatus.Failed,
                ingest: DesktopSaleReceiptIngestStatus.Unstamped),

        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "No fixture for this filter.")
    };

    private async Task SeedFiscalDayAsync(int deviceId, int fiscalDayNo, FiscalDayLifecycleStatus status)
    {
        _context.FiscalDayStates.Add(new FiscalDayStateEntity
        {
            DeviceId = deviceId,
            FiscalDayNo = fiscalDayNo,
            Status = status,
            OpenedAtLocal = new DateTime(2026, 8, 18, 6, 0, 0),
            MaxDurationHours = 24
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Everything <see cref="IInvoiceService"/> can be asked, refused. Each test overrides only the one
    /// call it is about, so a route reached by accident fails loudly instead of returning a plausible
    /// null.
    /// </summary>
    private abstract class StubInvoiceService : IInvoiceService
    {
        public virtual Task<InvoiceDto?> GetInvoiceByDocNumAsync(int docNum) => throw new NotSupportedException();

        public virtual Task<FiscalizationResult> FiscalizeInvoiceAsync(int docEntry) => throw new NotSupportedException();

        public Task<InvoiceListResponse?> GetInvoicesAsync(
            int page = 1, int pageSize = 20, int? docNum = null, string? cardCode = null,
            DateTime? fromDate = null, DateTime? toDate = null, bool? vanSalesOnly = null) =>
            throw new NotSupportedException();

        public Task<InvoiceDto?> GetInvoiceByDocEntryAsync(int docEntry) => throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetInvoicesByCustomerAsync(
            string cardCode, DateTime? fromDate = null, DateTime? toDate = null,
            int? page = null, int? pageSize = null, bool includeLines = false) =>
            throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetOpenInvoicesByCustomersAsync(IEnumerable<string> cardCodes) =>
            throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetInvoicesByDateAsync(DateTime date) => throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetInvoicesByDateRangeAsync(
            DateTime fromDate, DateTime toDate, int? page = null, int? pageSize = null) =>
            throw new NotSupportedException();

        public Task<(bool Success, string Message, InvoiceDto? Invoice, FiscalizationResult? Fiscalization)>
            CreateInvoiceAsync(CreateInvoiceRequest request) => throw new NotSupportedException();

        public Task<byte[]?> GetInvoicePdfAsync(int docEntry, string? fiscalQrCode = null) =>
            throw new NotSupportedException();
    }

    /// <summary>SAP has the invoice; the fiscalise call gives no answer.</summary>
    private sealed class ThrowingInvoiceService(Exception failure) : StubInvoiceService
    {
        public override Task<InvoiceDto?> GetInvoiceByDocNumAsync(int docNum) =>
            Task.FromResult<InvoiceDto?>(new InvoiceDto { DocEntry = 51204, DocNum = docNum });

        public override Task<FiscalizationResult> FiscalizeInvoiceAsync(int docEntry) => throw failure;
    }

    /// <summary>SAP has no such invoice, so nothing is ever submitted.</summary>
    private sealed class MissingInvoiceService : StubInvoiceService
    {
        public override Task<InvoiceDto?> GetInvoiceByDocNumAsync(int docNum) =>
            Task.FromResult<InvoiceDto?>(null);
    }

    /// <summary>A platform that answers every device the same way.</summary>
    private class StubFiscalisationClient : IFiscalisationApiClient
    {
        public int MaxHours { get; init; } = 24;

        public DateTime? FiscalDayOpened { get; init; }

        // Virtual, not hidden with `new`: the handler holds the interface, and a hidden method is not
        // reached through it — the refusal would never happen and the test would pass for the wrong reason.
        public virtual Task<FiscalConfigApiResponse> GetFiscalConfigAsync(int deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FiscalConfigApiResponse
            {
                DeviceSerialNo = $"ZW-FD-{deviceId:D7}",
                DeviceOperatingMode = "Offline",
                TaxPayerDayMaxHrs = MaxHours,
                CertificateValidTill = DateTime.UtcNow.AddDays(200)
            });

        public Task<FiscalStatusApiResponse> GetFiscalStatusAsync(int deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FiscalStatusApiResponse
            {
                DeviceId = deviceId,
                FiscalDayNo = 212,
                FiscalDayStatus = "FiscalDayOpened",
                FiscalDayOpened = FiscalDayOpened
            });

        public Task<FiscalConfigApiResponse> GetFiscalConfigWithApiKeyAsync(string? apiKey, int deviceId, CancellationToken cancellationToken = default) =>
            GetFiscalConfigAsync(deviceId, cancellationToken);

        public Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(SapFiscaliseReceiptApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SubmitReceiptApiResponse> SubmitReceiptAsync(SubmitReceiptApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PreflightReceiptApiResponse> PreflightReceiptAsync(SubmitReceiptApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PreflightReceiptApiResponse> PreflightSignedReceiptAsync(IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(int deviceId, string invoiceNo, ReceiptType receiptType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A platform that will not describe the device at all.</summary>
    private sealed class SilentFiscalisationClient : StubFiscalisationClient
    {
        public override Task<FiscalConfigApiResponse> GetFiscalConfigAsync(int deviceId, CancellationToken cancellationToken = default) =>
            throw new FiscalisationApiException(
                HttpStatusCode.NotFound, "DeviceNotFound", $"The platform has no device {deviceId} configured.");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
