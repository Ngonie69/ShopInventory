using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The transfer note printed for a factory transfer. Until the transfer is settled it is a draft:
/// stamped DRAFT, carrying the counts typed so far, and never passing an uncounted line off as zero.
/// Once settled it carries what was accepted, and a rejected or recalled transfer accepts nothing.
/// Set <c>FACTORY_NOTE_OUT</c> to a folder to keep the HTML for printing with headless Chrome.
/// </summary>
public sealed class FactoryTransferDocumentBuilderTests
{
    private const string DraftStamp = "<div class=\"mod-watermark ftd-watermark-draft\" aria-hidden=\"true\"><span>DRAFT</span></div>";
    private static readonly DateTime PrintedAtCat = new(2026, 11, 6, 9, 30, 0);

    [Theory]
    [InlineData(FactoryTransferStatus.Sending)]
    [InlineData(FactoryTransferStatus.SendFailed)]
    [InlineData(FactoryTransferStatus.InTransit)]
    [InlineData(FactoryTransferStatus.Settling)]
    [InlineData(FactoryTransferStatus.SettleFailed)]
    public void An_unsettled_transfer_prints_stamped_draft(string status)
    {
        var html = Build(InTransit(status), $"draft-{status}");

        Assert.Contains(DraftStamp, html);
        Assert.Contains("Factory Transfer FT-000042 — Draft", html);
        Assert.Contains("Units counted", html);
    }

    [Theory]
    [InlineData(FactoryTransferStatus.Accepted)]
    [InlineData(FactoryTransferStatus.PartiallyAccepted)]
    [InlineData(FactoryTransferStatus.Rejected)]
    [InlineData(FactoryTransferStatus.Recalled)]
    [InlineData(FactoryTransferStatus.Cancelled)]
    public void A_settled_transfer_is_not_a_draft(string status)
    {
        var transfer = Accepted();
        transfer.Summary.Status = status;

        var html = Build(transfer, $"settled-{status}");

        Assert.DoesNotContain("<span>DRAFT</span>", html);
        Assert.DoesNotContain("— Draft", html);
    }

    [Fact]
    public void A_draft_in_transit_says_who_it_waits_for()
    {
        var html = Build(InTransit(FactoryTransferStatus.InTransit), "in-transit");

        Assert.Contains("Draft — not yet accepted into DISPYOG", html);
        Assert.Contains("PROYG 2 → FSRW → DISPYOG", html);
        Assert.Contains("#70311", html);           // SAP out leg
        Assert.Contains("Not posted", html);       // SAP in leg
    }

    [Fact]
    public void A_send_that_never_reached_SAP_says_nothing_moved()
    {
        var transfer = InTransit(FactoryTransferStatus.SendFailed);
        transfer.Summary.OutSapDocNum = 0;

        var html = Build(transfer, "send-failed");

        Assert.Contains("Draft — not yet posted to SAP; no stock has moved", html);
    }

    [Fact]
    public void A_draft_carries_the_counts_typed_so_far()
    {
        var html = Build(InTransit(FactoryTransferStatus.InTransit), "draft-counts", new Dictionary<int, decimal> { [1] = 117 });

        Assert.Contains("−3 short", html);                          // line 1: 117 of 120
        Assert.Contains("mod-stat-value mod-num\">117<", html);     // units counted so far
        Assert.Contains("Not counted", html);                       // line 2 untouched
        Assert.Contains("mod-stat-label\">Difference</div><div class=\"mod-stat-value mod-num\">—<", html);
    }

    [Fact]
    public void An_uncounted_draft_reads_not_counted_rather_than_zero()
    {
        var html = Build(InTransit(FactoryTransferStatus.InTransit), "uncounted");

        Assert.Contains("Not counted", html);
        Assert.Contains("mod-stat-label\">Units counted</div><div class=\"mod-stat-value mod-num\">—<", html);
    }

    [Fact]
    public void A_part_accepted_transfer_shows_what_went_back()
    {
        var html = Build(Accepted(), "part-accepted");

        Assert.Contains("Units accepted", html);
        Assert.Contains("mod-stat-value mod-num\">178<", html);    // 118 + 60
        Assert.Contains("2 returned", html);
        Assert.Contains("Matches", html);
        Assert.Contains("Internal — factory transfer note", html);
        Assert.Contains("Two crates were leaking.", html);
    }

    [Fact]
    public void Typed_counts_never_override_a_settled_transfer()
    {
        var html = Build(Accepted(), "settled-counts", new Dictionary<int, decimal> { [1] = 1 });

        Assert.Contains("mod-stat-value mod-num\">178<", html);
        Assert.DoesNotContain("119 returned", html);
    }

    [Theory]
    [InlineData(FactoryTransferStatus.Rejected)]
    [InlineData(FactoryTransferStatus.Recalled)]
    public void A_rejected_or_recalled_transfer_accepts_nothing(string status)
    {
        var transfer = Accepted();
        transfer.Summary.Status = status;

        var html = Build(transfer, $"none-{status}");

        Assert.Contains("mod-stat-label\">Units accepted</div><div class=\"mod-stat-value mod-num\">0<", html);
        Assert.Contains("120 returned", html);
        Assert.Contains("60 returned", html);
    }

    [Fact]
    public void A_depot_truck_names_the_truck_driver_and_each_lines_source()
    {
        var transfer = Accepted();
        transfer.Summary.Direction = FactoryTransferStatus.Delivery;
        transfer.Summary.DepartmentName = "Graniteside";
        transfer.Summary.HoldingWarehouseCode = "DEPOTRAN";
        transfer.Summary.DestinationWarehouseCode = "KEFGRC";
        transfer.Summary.SourceLabel = "DISPYOG, DISPCH";
        transfer.Summary.VehiclePlate = "ACE 1234";
        transfer.Summary.DriverName = "Waiti";
        transfer.Summary.SourceRequestDocNum = 5521;
        transfer.Lines[1].FromWarehouseCode = "DISPCH";

        var html = Build(transfer, "truck");

        Assert.Contains("Graniteside delivery", html);
        Assert.Contains("ACE 1234", html);
        Assert.Contains("#5521", html);
        Assert.Contains("ftd-signs ftd-signs-3", html);
        Assert.Contains("Loaded by", html);
        Assert.Contains("Waiti · signature", html);
        Assert.Contains("<th class=\"ftd-w-from\">From</th>", html);
        Assert.Contains("DISPCH", html);
    }

    [Fact]
    public void An_unsent_transfer_prints_as_a_draft_with_nothing_posted()
    {
        var transfer = InTransit(FactoryTransferStatus.InTransit);
        transfer.Summary.Status = string.Empty;
        transfer.Summary.Number = string.Empty;
        transfer.Summary.OutSapDocNum = 0;
        transfer.Summary.SentByName = string.Empty;

        var html = FactoryTransferDocumentBuilder.BuildUnsent(transfer, PrintedAtCat);
        Keep(html, "unsent");

        Assert.Contains(DraftStamp, html);
        Assert.Contains("New Factory Transfer — Draft", html);
        Assert.Contains("Units to send", html);
        Assert.Contains("Draft — not sent; no stock has moved", html);
        Assert.Contains("Not sent", html);
        Assert.DoesNotContain("#70311", html);
    }

    [Fact]
    public void Text_the_sender_typed_is_escaped()
    {
        var transfer = Accepted();
        transfer.Summary.Reason = "<script>alert(1)</script> & shift 2";

        var html = Build(transfer, "escaped");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; shift 2", html);
    }

    private static string Build(FactoryTransferDetailDto transfer, string name, IReadOnlyDictionary<int, decimal>? draftCounts = null)
        => Keep(FactoryTransferDocumentBuilder.Build(transfer, PrintedAtCat, draftCounts), name);

    private static string Keep(string html, string name)
    {
        if (Environment.GetEnvironmentVariable("FACTORY_NOTE_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"factory-note-{name}.html"), html);
        }

        return html;
    }

    private static FactoryTransferDetailDto InTransit(string status)
    {
        var transfer = Accepted();
        transfer.Summary.Status = status;
        transfer.Summary.Decision = null;
        transfer.Summary.DecidedByName = null;
        transfer.Summary.DecidedAtUtc = null;
        transfer.Summary.InSapDocNum = 0;
        transfer.DecisionReason = null;
        foreach (var line in transfer.Lines)
            line.AcceptedQuantity = null;
        return transfer;
    }

    private static FactoryTransferDetailDto Accepted() => new()
    {
        Summary = new FactoryTransferSummaryDto
        {
            Id = 42,
            Number = "FT-000042",
            DepartmentCode = "YOG",
            DepartmentName = "Yoghurt",
            Direction = FactoryTransferStatus.Release,
            SourceWarehouseCode = "PROYG 2",
            SourceLabel = "PROYG 2",
            HoldingWarehouseCode = "FSRW",
            DestinationWarehouseCode = "DISPYOG",
            Reason = "End of shift 2 release",
            SentByName = "Rudo Chikwanha",
            SentAtUtc = new DateTime(2026, 11, 5, 14, 2, 0, DateTimeKind.Utc),
            Status = FactoryTransferStatus.PartiallyAccepted,
            Decision = "Accepted",
            DecidedByName = "Farai Ncube",
            DecidedAtUtc = new DateTime(2026, 11, 5, 15, 40, 0, DateTimeKind.Utc),
            OutSapDocNum = 70311,
            InSapDocNum = 70318,
            LineCount = 2,
            TotalQuantity = 180
        },
        DecisionReason = "Two crates were leaking.",
        Lines =
        [
            new()
            {
                Id = 1, LineNum = 0, ItemCode = "YOG144", ItemDescription = "6 Pack Vanilla Dairy Scoop Snack", UoMCode = "Crate",
                FromWarehouseCode = "PROYG 2", Quantity = 120, AcceptedQuantity = 118,
                Batches = [new() { BatchNumber = "YG251105A", Quantity = 80 }, new() { BatchNumber = "YG251105B", Quantity = 40 }]
            },
            new()
            {
                Id = 2, LineNum = 1, ItemCode = "CRA002", ItemDescription = "Black crate", FromWarehouseCode = "PROYG 2",
                Quantity = 60, AcceptedQuantity = 60
            }
        ]
    };
}
