using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The rules for changing an approved draft's quantities before it is added, and the SAP change they
/// turn into. Batches move by differences because SAP adds a PATCHed batch quantity to what the line
/// already holds (KEFALOS_TEST_3, 2026-11-09).
/// </summary>
public sealed class CreditNoteDraftQuantityPlanTests
{
    private static readonly List<SAPCreditNoteLine> Lines =
    [
        new() { LineNum = 0, ItemCode = "CRA001", Quantity = 100m },
        new() { LineNum = 1, ItemCode = "YOG005", Quantity = 26m }
    ];

    [Fact]
    public void Quantities_may_go_up_or_down_and_unchanged_lines_are_dropped()
    {
        var result = CreditNoteDraftQuantityPlan.Validate(Lines, [Edit(0, 80m), Edit(1, 30m)]);

        Assert.False(result.IsError);
        Assert.Collection(
            result.Value,
            change => Assert.Equal((0, 100m, 80m), (change.LineNum, change.From, change.To)),
            change => Assert.Equal((1, 26m, 30m), (change.LineNum, change.From, change.To)));

        var unchanged = CreditNoteDraftQuantityPlan.Validate(Lines, [Edit(0, 100m)]);
        Assert.Empty(unchanged.Value);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0, "-5")]
    [InlineData(0, "1.0000001")]
    [InlineData(7, "3")]
    public void Unusable_quantities_are_refused(int lineNum, string quantity)
    {
        var result = CreditNoteDraftQuantityPlan.Validate(Lines, [Edit(lineNum, decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture))]);

        Assert.True(result.IsError);
        Assert.Equal("CreditNoteApproval.InvalidQuantities", result.FirstError.Code);
    }

    [Fact]
    public void A_line_given_twice_is_refused()
    {
        var result = CreditNoteDraftQuantityPlan.Validate(Lines, [Edit(0, 80m), Edit(0, 90m)]);

        Assert.True(result.IsError);
    }

    [Fact]
    public void A_line_without_batches_changes_its_quantity_only()
    {
        var plan = CreditNoteDraftQuantityPlan.Build([Change(0, "CRA001", 100m, 80m)], Allocations(Line(0, 100m)));

        var change = Assert.Single(plan.Value);
        Assert.Equal(80m, change.Quantity);
        Assert.Empty(change.BatchDeltas);
    }

    /// <summary>The measured case: 26 → 24 on a line holding one batch of 26 sends −2 to that batch.</summary>
    [Fact]
    public void A_single_batch_moves_by_the_difference()
    {
        var plan = CreditNoteDraftQuantityPlan.Build(
            [Change(1, "YOG005", 26m, 24m)],
            Allocations(Line(1, 26m, batches: [("B-0911", 26m)])));

        var delta = Assert.Single(Assert.Single(plan.Value).BatchDeltas);
        Assert.Equal(("B-0911", -2m), (delta.BatchNumber, delta.Delta));
    }

    /// <summary>Batches count in the inventory unit: two boxes of twelve going to three adds twelve.</summary>
    [Fact]
    public void A_batch_moves_in_the_inventory_unit()
    {
        var plan = CreditNoteDraftQuantityPlan.Build(
            [Change(1, "YOG005", 2m, 3m)],
            Allocations(Line(1, 2m, inventoryQuantity: 24m, batches: [("B-0911", 24m)])));

        Assert.Equal(12m, Assert.Single(Assert.Single(plan.Value).BatchDeltas).Delta);
    }

    [Fact]
    public void A_line_split_across_batches_is_refused()
    {
        var plan = CreditNoteDraftQuantityPlan.Build(
            [Change(1, "YOG005", 26m, 24m)],
            Allocations(Line(1, 26m, batches: [("B-1", 20m), ("B-2", 6m)])));

        Assert.True(plan.IsError);
        Assert.Equal("CreditNoteApproval.QuantityNotEditable", plan.FirstError.Code);
        Assert.Contains("2 batches", plan.FirstError.Description);
    }

    [Fact]
    public void A_line_with_serial_numbers_is_refused()
    {
        var line = Line(1, 2m);
        line.SerialNumbers = [System.Text.Json.JsonDocument.Parse("{\"InternalSerialNumber\":\"S1\"}").RootElement];

        var plan = CreditNoteDraftQuantityPlan.Build([Change(1, "FRG001", 2m, 1m)], Allocations(line));

        Assert.True(plan.IsError);
        Assert.Equal("CreditNoteApproval.QuantityNotEditable", plan.FirstError.Code);
    }

    [Fact]
    public void Describe_lists_each_change()
    {
        Assert.Equal(
            "CRA001 100 → 80, CRA002 50 → 52.5",
            CreditNoteDraftQuantityPlan.Describe([Change(0, "CRA001", 100m, 80m), Change(1, "CRA002", 50m, 52.5m)]));
    }

    private static CreditNoteLineQuantityDto Edit(int lineNum, decimal quantity) => new() { LineNum = lineNum, Quantity = quantity };

    private static CreditNoteLineQuantityChangeDto Change(int lineNum, string item, decimal from, decimal to) =>
        new() { LineNum = lineNum, ItemCode = item, From = from, To = to };

    private static SAPDraftAllocations Allocations(params SAPDraftAllocationLine[] lines) => new() { DocEntry = 88123, DocumentLines = [.. lines] };

    private static SAPDraftAllocationLine Line(
        int lineNum,
        decimal quantity,
        decimal? inventoryQuantity = null,
        (string Batch, decimal Quantity)[]? batches = null) => new()
    {
        LineNum = lineNum,
        Quantity = quantity,
        InventoryQuantity = inventoryQuantity,
        BatchNumbers = batches?.Select(batch => new SAPDraftBatchAllocation { BatchNumber = batch.Batch, Quantity = batch.Quantity }).ToList()
    };
}
