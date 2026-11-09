using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The filters on the SAP-held transfers page become clauses on the draft header, so SAP narrows the
/// rows and the count together. These pin the exact OData each filter adds.
/// </summary>
public class SapTransferDraftFilterTests
{
    [Fact]
    public void No_filter_adds_nothing()
    {
        Assert.Equal(string.Empty, SAPServiceLayerClient.DraftQueueFilterClauses(null));
        Assert.Equal(string.Empty, SAPServiceLayerClient.DraftQueueFilterClauses(new SapTransferDraftFilter()));
        Assert.Equal(string.Empty, SAPServiceLayerClient.DraftQueueFilterClauses(new SapTransferDraftFilter
        {
            FromWarehouses = [],
            ToWarehouses = ["  "]
        }));
    }

    [Fact]
    public void Every_filter_becomes_one_clause_on_the_draft_header()
    {
        var clauses = SAPServiceLayerClient.DraftQueueFilterClauses(new SapTransferDraftFilter
        {
            Warehouse = " cmfs ",
            FromWarehouses = ["PROYG 2", "FSRW", "fsrw"],
            ToWarehouses = ["DISP'X"],
            CreatedFrom = new DateTime(2026, 11, 5, 14, 30, 0),
            CreatedTo = new DateTime(2026, 11, 8),
            AttachedOnly = true,
            DraftNum = 94410
        });

        Assert.Equal(
            " and (FromWarehouse eq 'cmfs' or ToWarehouse eq 'cmfs')"
            + " and (FromWarehouse eq 'FSRW' or FromWarehouse eq 'PROYG 2')"
            + " and (ToWarehouse eq 'DISP''X')"
            + " and CreationDate ge '2026-11-05'"
            + " and CreationDate le '2026-11-08'"
            + " and AttachmentEntry gt 0"
            + " and DocNum eq 94410",
            clauses);
    }

    [Fact]
    public void The_query_folds_its_warehouse_into_the_filter_it_sends()
    {
        var query = new GetSapTransferApprovalsQuery(1, 25, Warehouse: " QADH ", Filter: new SapTransferDraftFilter
        {
            Warehouse = "ignored",
            FromWarehouses = ["FSRW"],
            AttachedOnly = true
        });

        var filter = query.EffectiveFilter;
        Assert.Equal("QADH", filter.Warehouse);
        Assert.Equal(["FSRW"], filter.FromWarehouses);
        Assert.True(filter.AttachedOnly);

        Assert.Null(new GetSapTransferApprovalsQuery(1, 25).EffectiveFilter.Warehouse);
    }

    [Theory]
    [InlineData(21, 0, false, null, true)]
    [InlineData(20, 0, false, null, false)]
    [InlineData(1, 21, false, null, true)]
    [InlineData(1, 0, true, null, true)]
    [InlineData(1, 0, false, 0, true)]
    [InlineData(1, 0, false, 94410, false)]
    public void The_validator_bounds_the_filters(int fromCount, int codeLength, bool datesReversed, int? draftNum, bool refused)
    {
        var code = codeLength == 0 ? "FSRW" : new string('W', codeLength);
        var filter = new SapTransferDraftFilter
        {
            FromWarehouses = Enumerable.Range(0, fromCount).Select(index => index == 0 ? code : $"W{index}").ToList(),
            CreatedFrom = datesReversed ? new DateTime(2026, 11, 8) : new DateTime(2026, 11, 5),
            CreatedTo = datesReversed ? new DateTime(2026, 11, 5) : new DateTime(2026, 11, 8),
            DraftNum = draftNum
        };

        var result = new GetSapTransferApprovalsValidator().Validate(new GetSapTransferApprovalsQuery(1, 25, Filter: filter));

        Assert.Equal(refused, !result.IsValid);
    }
}
