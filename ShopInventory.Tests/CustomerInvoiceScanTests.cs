using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.CustomerDocuments.Commands.ScanNewInvoicesForDelivery;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The scan that queues automatic sends: what it queues, what it leaves alone, and that the watermark
/// and the rows only ever move together.
/// </summary>
public sealed class CustomerInvoiceScanTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    private readonly CustomerDocumentTestKit _kit = new();
    private readonly RecordingDispatchTrigger _trigger = new();
    private readonly List<Invoice> _sapInvoices = [];
    private readonly List<int> _readsAfter = [];
    private int? _latestDocEntry = 5000;

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task The_first_pass_only_records_where_SAP_is_so_no_earlier_invoice_is_ever_sent()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(4999));

        var result = await ScanAsync();

        Assert.Equal("Started", result.Outcome);
        Assert.Equal(5000, (await CheckpointAsync())!.LastDocEntry);
        Assert.Empty(_readsAfter);
        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task Only_numbers_marked_for_automatic_invoices_on_the_invoices_own_card_are_queued()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        var wanted = await _kit.AddContactAsync(phone: "+263771111111");
        await _kit.AddContactAsync(phone: "+263772222222", autoSend: false);
        await _kit.AddContactAsync(phone: "+263773333333", optedOutAtUtc: Now.AddDays(-1));
        await _kit.AddContactAsync(phone: "+263774444444", cardCode: "CHE012");
        var removed = await _kit.AddContactAsync(phone: "+263775555555");
        await using (var context = _kit.NewContext())
        {
            await context.CustomerWhatsAppContacts.Where(c => c.Id == removed.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.RemovedAtUtc, Now));
        }

        _sapInvoices.Add(Invoice(5001));

        var result = await ScanAsync();

        var queued = Assert.Single(await DeliveriesAsync());
        Assert.Equal(CustomerDocumentDeliveryTrigger.Auto, queued.Trigger);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, queued.Status);
        Assert.Equal(wanted.Id, queued.ContactId);
        Assert.Equal("+263771111111", queued.RecipientE164);
        Assert.Equal(5001, queued.SapDocEntry);
        Assert.Equal("785001", queued.DocumentNumber);
        Assert.Equal(125.50m, queued.DocumentTotal);
        Assert.Equal("KEF-WEB-5001", queued.SaleReference);
        Assert.Equal(1, result.Queued);
        Assert.Equal(1, _trigger.Triggered);
        Assert.Equal(5001, (await CheckpointAsync())!.LastDocEntry);
    }

    [Fact]
    public async Task With_automatic_sending_off_the_watermark_moves_and_nothing_is_written()
    {
        await _kit.SetRuntimeAsync(autoSend: false);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.AddRange([Invoice(5001), Invoice(5002)]);

        var result = await ScanAsync();

        Assert.Equal(2, result.Read);
        Assert.Equal(5002, (await CheckpointAsync())!.LastDocEntry);
        Assert.Empty(await DeliveriesAsync());
        Assert.Equal(0, _trigger.Triggered);
    }

    [Fact]
    public async Task The_overlap_reads_recent_invoices_again_but_never_queues_one_twice()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(5001));

        await ScanAsync();
        _sapInvoices.Add(Invoice(5002));
        await ScanAsync();

        Assert.Equal([4980, 4981], _readsAfter);
        var rows = await DeliveriesAsync();
        Assert.Equal([5001, 5002], rows.Select(row => row.SapDocEntry!.Value).OrderBy(x => x));
    }

    [Fact]
    public async Task A_straggler_posted_just_under_the_watermark_is_still_queued()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(4995));

        await ScanAsync();

        Assert.Equal(4995, Assert.Single(await DeliveriesAsync()).SapDocEntry);
        Assert.Equal(5000, (await CheckpointAsync())!.LastDocEntry);
    }

    [Theory]
    [InlineData("tYES", "csYes", "KEF-WEB-5001")]
    [InlineData("tNO", "csCancellation", "KEF-WEB-5001")]
    [InlineData("tNO", "csNo", "CONSOL-20261009-SPA002")]
    public async Task A_cancelled_or_consolidated_invoice_writes_no_row(string cancelled, string cancelStatus, string saleReference)
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(5001, cancelled: cancelled, cancelStatus: cancelStatus, saleReference: saleReference));

        await ScanAsync();

        Assert.Empty(await DeliveriesAsync());
        Assert.Equal(5001, (await CheckpointAsync())!.LastDocEntry);
    }

    [Fact]
    public async Task An_invoice_on_a_selling_account_writes_no_row_even_with_a_number_on_the_card()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        await using (var context = _kit.NewContext())
        {
            context.Shops.Add(new ShopEntity { Code = "SPA", Name = "Spar till", BusinessPartnerCode = CustomerDocumentTestKit.CardCode, WarehouseCode = "SPA" });
            await context.SaveChangesAsync();
        }

        _sapInvoices.Add(Invoice(5001));

        await ScanAsync();

        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task A_reposted_invoice_is_recorded_as_skipped_not_sent()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(5001, comments: "Invoice posted from SAP update. Old invoice 771234."));

        var result = await ScanAsync();

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(CustomerDocumentDeliveryStatus.Skipped, row.Status);
        Assert.Contains("Reposted", row.StatusReason);
        Assert.NotNull(row.ClosedAtUtc);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, _trigger.Triggered);
    }

    [Fact]
    public async Task An_invoice_dated_more_than_a_week_back_is_recorded_as_skipped()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(5001, docDate: "2026-10-01T00:00:00Z"));
        _sapInvoices.Add(Invoice(5002, docDate: "2026-10-02T00:00:00Z"));

        await ScanAsync();

        var rows = (await DeliveriesAsync()).OrderBy(row => row.SapDocEntry).ToList();
        Assert.Equal(CustomerDocumentDeliveryStatus.Skipped, rows[0].Status);
        Assert.Equal(CustomerDocumentDeliveryStatus.Pending, rows[1].Status);
    }

    [Fact]
    public async Task A_backlog_is_read_a_page_at_a_time_up_to_the_cap_and_finished_on_the_next_pass()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.AddRange(Enumerable.Range(5001, 5).Select(docEntry => Invoice(docEntry)));
        var settings = CustomerDocumentTestKit.Settings(s =>
        {
            s.InvoiceScanPageSize = 2;
            s.InvoiceScanMaxPagesPerPass = 2;
            s.InvoiceScanOverlap = 0;
        });

        await ScanAsync(settings);
        Assert.Equal(5004, (await CheckpointAsync())!.LastDocEntry);

        await ScanAsync(settings);
        Assert.Equal(5005, (await CheckpointAsync())!.LastDocEntry);
        Assert.Equal(5, (await DeliveriesAsync()).Count);
    }

    [Fact]
    public async Task A_failed_save_keeps_neither_the_rows_nor_the_new_watermark()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);
        await _kit.AddContactAsync();
        _sapInvoices.Add(Invoice(5001));

        await Assert.ThrowsAsync<DbUpdateException>(() => ScanAsync(interceptor: new FailingSave()));

        Assert.Empty(await DeliveriesAsync());
        Assert.Equal(5000, (await CheckpointAsync())!.LastDocEntry);
    }

    [Fact]
    public async Task An_unreadable_checkpoint_starts_again_from_the_newest_invoice()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await _kit.AddContactAsync();
        await using (var context = _kit.NewContext())
        {
            context.SystemConfigs.Add(new SystemConfigEntity { Key = InvoiceScanCheckpoint.Key, Value = "{not json" });
            await context.SaveChangesAsync();
        }

        _latestDocEntry = 6200;
        _sapInvoices.Add(Invoice(6100));

        var result = await ScanAsync();

        Assert.Equal("Started", result.Outcome);
        Assert.Equal(6200, (await CheckpointAsync())!.LastDocEntry);
        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task No_SAP_call_is_made_while_SAP_is_held_back()
    {
        await _kit.SetRuntimeAsync(autoSend: true);
        await SetCheckpointAsync(5000);

        await using var context = _kit.NewContext();
        var handler = Handler(context, SapAnswers.Create(new()), CustomerDocumentTestKit.Settings());

        var result = await handler.Handle(new ScanNewInvoicesForDeliveryCommand(SapHeldBack: true), CancellationToken.None);

        Assert.Equal("SAP held back", result.Value.Outcome);
        Assert.Equal(5000, (await CheckpointAsync())!.LastDocEntry);
    }

    [Fact]
    public void A_route_customers_invoice_on_a_vans_card_is_never_the_scans_to_send()
    {
        // Documented as a classifier case so the reason is pinned, not only the outcome.
        var decision = InvoiceDeliveryClassifier.Classify(
            Invoice(5001), isConsolidated: false, isSellingAccount: true, isReposted: false,
            todayCat: Now.Date, CustomerDocumentTestKit.Settings());

        Assert.Equal(InvoiceDeliveryDecisionKind.Ignore, decision.Kind);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private async Task<ScanNewInvoicesForDeliveryResult> ScanAsync(
        CustomerDocumentDeliverySettings? settings = null,
        IInterceptor? interceptor = null)
    {
        var sap = SapAnswers.Create(new()
        {
            ["GetLatestInvoiceDocEntryAsync"] = _ => Task.FromResult(_latestDocEntry),
            ["GetInvoiceDeliveryHeadersAfterDocEntryAsync"] = args =>
            {
                var after = (int)args![0]!;
                var top = (int)args[1]!;
                _readsAfter.Add(after);
                return Task.FromResult(_sapInvoices
                    .Where(invoice => invoice.DocEntry > after)
                    .OrderBy(invoice => invoice.DocEntry)
                    .Take(top)
                    .ToList());
            }
        });

        await using var context = interceptor is null ? _kit.NewContext() : _kit.NewContext(interceptor);
        var result = await Handler(context, sap, settings ?? CustomerDocumentTestKit.Settings())
            .Handle(new ScanNewInvoicesForDeliveryCommand(), CancellationToken.None);
        return result.Value;
    }

    private ScanNewInvoicesForDeliveryHandler Handler(
        ShopInventory.Data.ApplicationDbContext context,
        ShopInventory.Services.ISAPServiceLayerClient sap,
        CustomerDocumentDeliverySettings settings) => new(
            context,
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(settings),
            Options.Create(new FiscalisationSettings { RepostedInvoiceCommentsPrefix = "Invoice posted from SAP update." }),
            _trigger,
            new FakePacer(Now),
            NullLogger<ScanNewInvoicesForDeliveryHandler>.Instance);

    private async Task SetCheckpointAsync(int lastDocEntry)
    {
        await using var context = _kit.NewContext();
        await new InvoiceScanCheckpoint { LastDocEntry = lastDocEntry, InitialisedAtUtc = Now.AddDays(-1), LastScanAtUtc = Now.AddMinutes(-2) }
            .StageAsync(context, CancellationToken.None);
        await context.SaveChangesAsync();
    }

    private async Task<InvoiceScanCheckpoint?> CheckpointAsync()
    {
        await using var context = _kit.NewContext();
        return await InvoiceScanCheckpoint.ReadAsync(context, CancellationToken.None);
    }

    private async Task<List<CustomerDocumentDeliveryEntity>> DeliveriesAsync()
    {
        await using var context = _kit.NewContext();
        return await context.CustomerDocumentDeliveries.AsNoTracking().ToListAsync();
    }

    private static Invoice Invoice(
        int docEntry,
        string cancelled = "tNO",
        string cancelStatus = "csNo",
        string? saleReference = null,
        string? comments = null,
        string docDate = "2026-10-09T00:00:00Z") => new()
        {
            DocEntry = docEntry,
            DocNum = 780000 + docEntry,
            DocDate = docDate,
            CardCode = CustomerDocumentTestKit.CardCode,
            CardName = "Spar Bridge",
            DocTotal = 125.50m,
            DocCurrency = "USD",
            Cancelled = cancelled,
            CancelStatus = cancelStatus,
            U_Van_saleorder = saleReference ?? $"KEF-WEB-{docEntry}",
            Comments = comments
        };

    private sealed class FailingSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("The database refused the save.");
    }
}
