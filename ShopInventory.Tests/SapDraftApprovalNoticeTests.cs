using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The B1 inbox message that tells SAP's approvers a transfer draft is waiting to be added: SAP opens its
/// approval only when a B1 user adds the draft (re-tested on KEFALOS_TEST_3 on 2026-11-19), so the
/// approvers have to hear of it some other way.
/// </summary>
public class SapDraftApprovalNoticeTests
{
    // Shaped on production's #82 "Transfers to Yoghurt" (stage 39) and the test company's stage 16
    // "Warehouse" (approvers Opsassist and Whouse).
    private static readonly SAPStockTransferDraft Draft = new()
    {
        DocEntry = 80193,
        DocNum = 88559,
        FromWarehouse = "WASHBAY",
        ToWarehouse = "PROYG 2"
    };

    private sealed class FakeSap
    {
        public Dictionary<int, int[]> Stages { get; init; } = new() { [39] = [6, 7] };
        public Dictionary<int, string> Users { get; init; } = new() { [6] = "Opsassist", [7] = "Whouse", [9] = "PdnAdmin" };
        public Exception? SendFailure { get; init; }
        public List<int> StagesRead { get; } = [];
        public List<SapInboxMessage> Sent { get; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetApprovalStageAsync) => Stage((int)args![0]!),
                nameof(ISAPServiceLayerClient.GetSapUsersAsync) => Task.FromResult(
                    ((IEnumerable<int>)args![0]!).Where(Users.ContainsKey)
                        .Select(key => new SAPUser { InternalKey = key, UserCode = Users[key] }).ToList()),
                nameof(ISAPServiceLayerClient.SendInboxMessageAsync) => Send((SapInboxMessage)args![0]!),
                _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
            });

        private Task<SAPApprovalStage?> Stage(int code)
        {
            StagesRead.Add(code);
            return Task.FromResult<SAPApprovalStage?>(Stages.TryGetValue(code, out var users)
                ? new SAPApprovalStage
                {
                    Code = code,
                    ApprovalStageApprovers = users.Select(user => new SAPApprovalStageApprover { UserID = user }).ToList()
                }
                : null);
        }

        private Task<int?> Send(SapInboxMessage message)
        {
            if (SendFailure is not null) throw SendFailure;
            Sent.Add(message);
            return Task.FromResult<int?>(4242);
        }
    }

    private static SapTransferRoute Covered(params SapTransferTemplateMatch[] templates)
        => new(SapTransferRouteKind.Covered, templates);

    private static SapTransferTemplateMatch Yoghurt(int[]? stages = null, int[]? originators = null)
        => new(82, "Transfers to Yoghurt", stages ?? [39], originators ?? [6, 7, 9]);

    private static Task<SapDraftNoticeResult> Send(FakeSap sap, SapTransferRoute route)
        => new SapDraftApprovalNotice(sap.AsClient(), NullLogger<SapDraftApprovalNotice>.Instance)
            .SendAsync(Draft, route, "Transfer request #11067 (WASHBAY to PROYG 2)", "ITR-23188", CancellationToken.None);

    [Fact]
    public async Task The_approvers_on_the_templates_stage_get_one_message_linking_to_the_draft()
    {
        var sap = new FakeSap();

        var result = await Send(sap, Covered(Yoghurt()));

        Assert.True(result.Sent);
        Assert.Equal(["Opsassist", "Whouse"], result.Recipients);
        Assert.Empty(result.NotOriginators);
        var message = Assert.Single(sap.Sent);
        Assert.Equal(["Opsassist", "Whouse"], message.RecipientUserCodes);

        // The link opens the draft (object 112) by DocEntry; draft numbers repeat, so it is never the key.
        Assert.Equal(new SapInboxLink("Draft", "88559", "112", "80193"), message.Link);
        Assert.Equal("Transfer draft 88559 waiting for approval: WASHBAY to PROYG 2", message.Subject);
        Assert.Contains("Transfer request #11067 (WASHBAY to PROYG 2)", message.Text);
        Assert.Contains("draft 88559 (order number ITR-23188)", message.Text);
        Assert.Contains("press Add", message.Text);
        Assert.Contains("'Transfers to Yoghurt'", message.Text);
        Assert.Contains("(Opsassist, Whouse) have a message in the SAP inbox", result.Sentence);
    }

    [Fact]
    public async Task Two_templates_sharing_an_approver_message_them_once()
    {
        var sap = new FakeSap { Stages = new() { [39] = [6, 7], [16] = [7, 9] } };

        var result = await Send(sap, Covered(Yoghurt(), new SapTransferTemplateMatch(36, "Trans From Warehouse", [16], [6, 7, 9])));

        Assert.Equal([39, 16], sap.StagesRead);
        var message = Assert.Single(sap.Sent);
        Assert.Equal(["Opsassist", "PdnAdmin", "Whouse"], message.RecipientUserCodes);
        Assert.Equal(["Opsassist", "PdnAdmin", "Whouse"], result.Recipients);
        Assert.Contains("'Transfers to Yoghurt' and 'Trans From Warehouse'", message.Text);
    }

    [Fact]
    public async Task An_approver_who_is_not_an_originator_is_still_messaged_and_named()
    {
        // Their Add would post the draft as an ordinary transfer: SAP holds only its originators' documents.
        var sap = new FakeSap();

        var result = await Send(sap, Covered(Yoghurt(originators: [6])));

        Assert.True(result.Sent);
        Assert.Equal(["Opsassist", "Whouse"], result.Recipients);
        Assert.Equal(["Whouse"], result.NotOriginators);
    }

    [Fact]
    public async Task A_template_with_no_stage_sends_nothing_and_says_so()
    {
        var sap = new FakeSap();

        var result = await Send(sap, Covered(Yoghurt(stages: [])));

        Assert.False(result.Sent);
        Assert.Empty(sap.Sent);
        Assert.Contains("names no approval stage", result.Problem);
        Assert.StartsWith("No SAP inbox message was sent", result.Sentence);
    }

    [Fact]
    public async Task A_stage_with_no_approver_sends_nothing_and_says_so()
    {
        var sap = new FakeSap { Stages = new() { [39] = [] } };

        var result = await Send(sap, Covered(Yoghurt()));

        Assert.False(result.Sent);
        Assert.Empty(sap.Sent);
        Assert.Contains("no approver", result.Problem);
    }

    [Fact]
    public async Task A_refused_message_never_fails_the_conversion()
    {
        // The draft is already saved; the message failing must not turn that into an error.
        var sap = new FakeSap { SendFailure = new HttpRequestException("SAP refused: -1 Invalid user") };

        var result = await Send(sap, Covered(Yoghurt()));

        Assert.False(result.Sent);
        Assert.Contains("Invalid user", result.Problem);
        Assert.StartsWith("No SAP inbox message was sent", result.Sentence);
    }

    [Fact]
    public void A_long_route_keeps_the_subject_inside_what_B1_shows()
    {
        var draft = new SAPStockTransferDraft
        {
            DocEntry = 1, DocNum = 2, FromWarehouse = new string('F', 80), ToWarehouse = new string('T', 80)
        };

        var message = SapDraftApprovalNotice.Compose(draft, Covered(Yoghurt()), "x", null, ["Whouse"]);

        Assert.Equal(SapDraftApprovalNotice.SubjectLength, message.Subject.Length);
        Assert.Contains("inventory transfer draft 2,", message.Text);
    }

    [Fact]
    public void A_matched_template_carries_its_stages_in_order_and_its_originators()
    {
        var template = new SAPApprovalTemplateCondition
        {
            Code = 82,
            Name = "Transfers to Yoghurt",
            IsActive = "tYES",
            UseTerms = "tYES",
            ApprovalTemplateQueries = [new SAPApprovalTemplateQuery { QueryID = 920 }],
            ApprovalTemplateStages =
            [
                new SAPApprovalTemplateStage { SortID = 2, ApprovalStageCode = 40 },
                new SAPApprovalTemplateStage { SortID = 1, ApprovalStageCode = 39 }
            ],
            ApprovalTemplateUsers = [new SAPApprovalTemplateUser { UserID = 6 }, new SAPApprovalTemplateUser { UserID = 9 }]
        };
        var queries = new Dictionary<int, List<SAPUserQuery>>
        {
            [920] = [new SAPUserQuery { InternalKey = 920, Query = "SELECT Distinct 'TRUE' FROM OWTR T0 WHERE T0.\"ToWhsCode\"= $[OWTR.ToWhsCode] and T0.\"ToWhsCode\" = 'PROYG 2'" }]
        };

        var verdict = SapTransferApprovalRoutes.Verdict([SapTransferApprovalRoutes.Compile(template, queries)], "WASHBAY", "PROYG 2");

        var match = Assert.Single(verdict.Templates);
        Assert.Equal([39, 40], match.StageCodes);
        Assert.Equal([6, 9], match.OriginatorIds);
    }
}
