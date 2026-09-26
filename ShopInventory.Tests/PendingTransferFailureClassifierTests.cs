using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;

namespace ShopInventory.Tests;

/// <summary>
/// The classifier reads the poster's own messages, so these build the messages the way the poster
/// does — from <see cref="StockValidationError.Message"/> — rather than retyping them. A reworded
/// message then fails here instead of silently turning every shortage into "Other".
/// </summary>
public sealed class PendingTransferFailureClassifierTests
{
    [Fact]
    public void A_shortage_reads_back_every_short_line_with_its_figures()
    {
        var message = "Insufficient stock in source warehouse: " + string.Join("; ",
            Short("YOG100", 360, 0).Message,
            Short("YOG017", 34, 10).Message,
            new StockValidationError
            {
                ItemCode = "MLK201", BatchNumber = "B-7", WarehouseCode = "KEFBYC",
                RequestedQuantity = 12.5m, AvailableQuantity = 2m
            }.Message);

        var failure = PendingTransferFailureClassifier.Classify(message);

        Assert.Equal(PendingTransferFailureKinds.StockShort, failure.Kind);
        Assert.False(failure.ShortLinesIncomplete);
        Assert.Collection(failure.ShortLines,
            line =>
            {
                Assert.Equal("YOG100", line.ItemCode);
                Assert.Equal("KEFBYC", line.WarehouseCode);
                Assert.Equal(360, line.RequestedQuantity);
                Assert.Equal(360, line.Shortage);
            },
            line => Assert.Equal(24, line.Shortage),
            line =>
            {
                Assert.Equal("B-7", line.BatchNumber);
                Assert.Equal(10.5m, line.Shortage);
            });
    }

    [Fact]
    public void A_shortage_cut_off_at_the_stored_length_says_its_list_is_incomplete()
    {
        var message = "Insufficient stock in source warehouse: " + string.Join("; ",
            Enumerable.Range(1, 60).Select(index => Short($"ITEM{index:000}", 100, 0).Message));
        message = message[..PendingTransferFailureClassifier.StoredErrorLength];

        var failure = PendingTransferFailureClassifier.Classify(message);

        Assert.True(failure.ShortLinesIncomplete);
        Assert.NotEmpty(failure.ShortLines);
    }

    [Fact]
    public void A_timed_out_post_is_an_unknown_outcome_not_a_failure_to_retry()
    {
        var failure = PendingTransferFailureClassifier.Classify(
            "The SAP post timed out before SAP answered, so it is not known whether the transfer was created. "
            + "Check SAP for this transfer before retrying — retrying will post it again.");

        Assert.Equal(PendingTransferFailureKinds.OutcomeUnknown, failure.Kind);
    }

    [Fact]
    public void An_unread_warehouse_is_not_a_shortage()
    {
        var failure = PendingTransferFailureClassifier.Classify(
            "Could not read stock from SAP for warehouse(s) KEFBYC. The transfer has not been posted; retry it once SAP is answering.");

        Assert.Equal(PendingTransferFailureKinds.StockUnread, failure.Kind);
        Assert.Empty(failure.ShortLines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Item YOG100 is inactive (-10)")]
    public void Anything_else_is_other(string? message)
    {
        Assert.Equal(PendingTransferFailureKinds.Other, PendingTransferFailureClassifier.Classify(message).Kind);
    }

    private static StockValidationError Short(string itemCode, decimal requested, decimal available) => new()
    {
        ItemCode = itemCode,
        WarehouseCode = "KEFBYC",
        RequestedQuantity = requested,
        AvailableQuantity = available
    };
}
