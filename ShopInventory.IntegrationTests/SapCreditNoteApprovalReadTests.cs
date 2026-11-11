using ShopInventory.Models;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Asks a real Service Layer to accept the approval-procedure reads behind the credit note approvals
/// page: the request list and its count, one request with its lines, the draft behind it, the stage,
/// template and user it names, and the bytes of an attached file.
/// </summary>
/// <remarks>
/// Read-only. The two writes — recording a decision and saving the draft to a document — change the
/// company permanently and are proven by hand against the test company, never here.
///
/// The <c>$select</c> lists pass the metadata check in <c>SapSelectClauseTests</c>, but SAP rejects
/// fields per entity set at runtime (it refused <c>BaseEntry</c> on <c>CreditNotes</c> while the shared
/// <c>Document</c> type declares it), and <c>Drafts</c> is exactly such a set. This is where that is
/// settled.
/// </remarks>
[Collection("SAP")]
public class SapCreditNoteApprovalReadTests(SapClientFixture fixture)
{
    private static readonly string[] OpenStatuses =
    [
        SapApprovalRequestStatuses.Pending,
        SapApprovalRequestStatuses.Approved
    ];

    private static readonly string[] AllStatuses =
    [
        SapApprovalRequestStatuses.Pending,
        SapApprovalRequestStatuses.Approved,
        SapApprovalRequestStatuses.NotApproved,
        SapApprovalRequestStatuses.Generated,
        SapApprovalRequestStatuses.GeneratedByAuthorizer,
        SapApprovalRequestStatuses.Cancelled
    ];

    [SapFact]
    public async Task Credit_note_approval_request_list_and_count_are_accepted()
    {
        var (items, total) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(OpenStatuses, 1, 5);

        Assert.True(total >= items.Count, $"SAP counted {total} requests but returned {items.Count} on the first page.");
        Assert.All(items, item => Assert.Equal(SapObjectTypes.CreditNote, item.ObjectType));
    }

    /// <summary>
    /// The two filters the wash bay's stage scope rests on: stages found by name, and the queue narrowed
    /// to a stage. Neither is proven by the metadata, and a Service Layer that refused either would take
    /// the wash bay's page down.
    /// </summary>
    [SapFact]
    public async Task The_queue_filters_on_current_stage_and_stages_are_found_by_name()
    {
        var (items, _) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(AllStatuses, 1, 20);
        var staged = items.FirstOrDefault(item => item.CurrentStage is not null);
        Assert.False(staged is null, "None of the newest 20 credit memo approval requests carries a stage, so the stage filter cannot be exercised.");

        var stage = await fixture.Client.GetApprovalStageAsync(staged!.CurrentStage!.Value);
        Assert.NotNull(stage);
        Assert.False(string.IsNullOrWhiteSpace(stage.Name));

        var byName = await fixture.Client.GetApprovalStagesByNameAsync([stage.Name!]);
        Assert.Contains(byName, found => found.Code == stage.Code);

        var (scoped, scopedTotal) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(AllStatuses, 1, 5, stageCodes: [stage.Code]);
        Assert.NotEmpty(scoped);
        Assert.All(scoped, item => Assert.Equal(stage.Code, item.CurrentStage));
        Assert.True(scopedTotal >= scoped.Count, $"SAP counted {scopedTotal} requests at stage {stage.Code} but returned {scoped.Count}.");
    }

    /// <summary>
    /// The queue's filters: raised dates and originator in the rows and the count, and the grouped read
    /// behind the Raised by choices. Narrowed to the newest request's own day and originator, so the
    /// filter has something to find whatever the company holds.
    /// </summary>
    [SapFact]
    public async Task The_raised_date_and_originator_filters_and_the_originator_list_are_accepted()
    {
        var originators = await fixture.Client.GetCreditNoteApprovalOriginatorIdsAsync(AllStatuses);
        Assert.NotEmpty(originators);

        var (newest, _) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(AllStatuses, 1, 1);
        var row = Assert.Single(newest);
        Assert.NotNull(row.OriginatorID);
        Assert.Contains(row.OriginatorID.Value, originators);

        var day = RaisedDay(row);
        var filter = new CreditNoteApprovalRequestFilter(day, day, row.OriginatorID);

        var (items, total) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(AllStatuses, 1, 5, filter: filter);
        Assert.Contains(items, item => item.Code == row.Code);
        Assert.All(items, item =>
        {
            Assert.Equal(row.OriginatorID, item.OriginatorID);
            Assert.Equal(day, RaisedDay(item));
        });

        var count = await fixture.Client.CountCreditNoteApprovalRequestsAsync(AllStatuses, filter: filter);
        var everything = await fixture.Client.CountCreditNoteApprovalRequestsAsync(AllStatuses);
        Assert.Equal(total, count);
        Assert.True(count < everything, $"The filter counted {count} of {everything}: it narrowed nothing.");
    }

    private static DateTime RaisedDay(SAPApprovalRequest request) =>
        DateTime.ParseExact(request.CreationDate![..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The two reads an add uses to recognise the credit note a draft became: the newest DocEntry in the
    /// company before the add, and the customer's newest past it afterwards — whole, with its lines, so
    /// it is not read a second time.
    /// </summary>
    [SapFact]
    public async Task The_reads_that_recognise_an_added_credit_note_are_accepted()
    {
        var newest = await fixture.Client.GetNewestCreditNoteDocEntryAsync();
        Assert.False(newest is null, "SAP holds no credit notes at all, so the add's identification reads cannot be exercised.");

        var top = await fixture.Client.GetCreditNoteByDocEntryAsync(newest!.Value);
        Assert.NotNull(top);
        Assert.False(string.IsNullOrWhiteSpace(top.CardCode));

        // Just below the newest of this customer's credit notes, so the filter must find exactly it.
        var found = await fixture.Client.GetNewestCreditNoteForCustomerAsync(top.CardCode!, newest.Value - 1);
        Assert.NotNull(found);
        Assert.Equal(newest.Value, found.DocEntry);
        Assert.NotNull(found.DocumentLines);
        Assert.NotEmpty(found.DocumentLines);

        // Nothing past the newest: the filter is honoured rather than ignored.
        Assert.Null(await fixture.Client.GetNewestCreditNoteForCustomerAsync(top.CardCode!, newest.Value));
    }

    /// <summary>
    /// The read behind the already-credited flag: credit notes by customer, exact total and entry day,
    /// with cancellations left out in SAP. Proven on whatever the company holds by asking for the
    /// newest credit note's own customer and total — it must come back, and must not once the day
    /// bound passes it.
    /// </summary>
    [SapFact]
    public async Task The_already_credited_read_finds_a_credit_note_by_its_customer_total_and_day()
    {
        var newest = await fixture.Client.GetNewestCreditNoteDocEntryAsync();
        Assert.False(newest is null, "SAP holds no credit notes at all, so the already-credited read cannot be exercised.");

        var top = await fixture.Client.GetCreditNoteByDocEntryAsync(newest!.Value);
        Assert.NotNull(top);
        Assert.False(string.IsNullOrWhiteSpace(top.CardCode));

        var found = await fixture.Client.GetCreditNotesByCustomerTotalsAsync(
            [new SapCreditNoteTotalKey(top.CardCode!, top.DocTotal, new DateTime(2000, 1, 1))]);

        Assert.All(found, note =>
        {
            Assert.Equal(top.CardCode, note.CardCode);
            Assert.Equal(top.DocTotal, note.DocTotal);
            Assert.Equal("csNo", note.CancelStatus);
            Assert.False(string.IsNullOrWhiteSpace(note.CreationDate), $"Credit note {note.DocEntry} came back without CreationDate.");
        });

        // The newest may itself be a cancellation, which the read rightly leaves out.
        if (!string.Equals(top.CancelStatus, "csNo", StringComparison.OrdinalIgnoreCase))
        {
            Assert.DoesNotContain(found, note => note.DocEntry == top.DocEntry);
            return;
        }

        var itself = Assert.Single(found, note => note.DocEntry == top.DocEntry);
        var entered = DateTime.ParseExact(itself.CreationDate![..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var onTheDay = await fixture.Client.GetCreditNotesByCustomerTotalsAsync(
            [new SapCreditNoteTotalKey(top.CardCode!, top.DocTotal, entered)]);
        Assert.Contains(onTheDay, note => note.DocEntry == top.DocEntry);

        var dayAfter = await fixture.Client.GetCreditNotesByCustomerTotalsAsync(
            [new SapCreditNoteTotalKey(top.CardCode!, top.DocTotal, entered.AddDays(1))]);
        Assert.DoesNotContain(dayAfter, note => note.DocEntry == top.DocEntry);
    }

    /// <summary>
    /// The index the open queues hide by, built against the live company: the drafts SAP still holds,
    /// the already-credited read over them, and the requests of those it finds. The held-drafts filter
    /// (DocumentStatus and AuthorizationStatus on Drafts) and the DraftEntry filter on ApprovalRequests
    /// are both settled here.
    /// </summary>
    [SapFact]
    public async Task The_already_credited_index_builds_from_the_drafts_sap_still_holds()
    {
        var held = await fixture.Client.GetOpenHeldCreditNoteDraftsAsync();
        Assert.All(held, draft =>
        {
            Assert.Equal(SapDocObjectCodes.CreditNotes, draft.DocObjectCode);
            Assert.Equal(SapDocumentStatuses.Open, draft.DocumentStatus);
            Assert.Contains(draft.AuthorizationStatus, new[] { SapDocumentAuthorizationStatuses.Pending, SapDocumentAuthorizationStatuses.Approved });
        });

        if (held.Count > 0)
        {
            var some = held.Take(30).Select(draft => draft.DocEntry).ToList();
            var requests = await fixture.Client.GetCreditNoteApprovalRequestsForDraftsAsync(OpenStatuses, some);
            Assert.All(requests, request =>
            {
                Assert.Contains(request.DraftEntry ?? 0, some);
                Assert.Contains(request.Status, OpenStatuses);
            });
        }

        var index = await new ShopInventory.Features.CreditNoteApprovals.CreditNoteAlreadyCreditedIndex(
                fixture.Client,
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ShopInventory.Features.CreditNoteApprovals.CreditNoteAlreadyCreditedIndex>.Instance)
            .GetAsync(CancellationToken.None);

        Assert.All(index.Values, entry =>
        {
            Assert.Contains(entry.Status, OpenStatuses);
            Assert.Equal("csNo", entry.CreditNote.CancelStatus);
        });
    }

    [SapFact]
    public async Task A_request_reads_back_with_its_draft_stage_template_originator_and_attachment()
    {
        var (items, _) = await fixture.Client.GetCreditNoteApprovalRequestsAsync(AllStatuses, 1, 1);
        var first = items.FirstOrDefault();
        Assert.False(
            first is null,
            "SAP holds no credit memo approval requests at all, so the detail reads cannot be exercised. Point these tests at a company where a credit memo has been through the approval procedure.");

        var request = await fixture.Client.GetApprovalRequestAsync(first!.Code);
        Assert.NotNull(request);
        Assert.Equal(first.Code, request.Code);
        Assert.NotNull(request.ApprovalRequestLines);

        if (request.CurrentStage is int stageCode)
        {
            var stage = await fixture.Client.GetApprovalStageAsync(stageCode);
            Assert.NotNull(stage);
            Assert.NotNull(stage.ApprovalStageApprovers);
        }

        if (request.ApprovalTemplatesID is int templateCode)
        {
            Assert.NotNull(await fixture.Client.GetApprovalTemplateAsync(templateCode));
        }

        if (request.OriginatorID is int originator)
        {
            var user = await fixture.Client.GetSapUserAsync(originator);
            Assert.NotNull(user);
            Assert.False(string.IsNullOrWhiteSpace(user.UserCode));
        }

        if (request.DraftEntry is not int draftEntry)
        {
            return;
        }

        // The Drafts set is the one that may reject a field the metadata allows.
        var draft = await fixture.Client.GetCreditNoteDraftAsync(draftEntry);
        var headers = await fixture.Client.GetCreditNoteDraftsAsync([draftEntry]);

        if (draft is null)
        {
            // A generated or cancelled request may have lost its draft; that is SAP's data, not a fault here.
            Assert.Empty(headers);
            return;
        }

        Assert.Equal(SapDocObjectCodes.CreditNotes, draft.DocObjectCode);

        // The already-credited check bounds its search by the day the draft was keyed.
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(headers).CreationDate), "The draft headers came back without CreationDate.");
        Assert.False(string.IsNullOrWhiteSpace(draft.CreationDate), "The draft came back without CreationDate.");

        if (draft.AttachmentEntry is not int attachmentEntry)
        {
            return;
        }

        var attachment = await fixture.Client.GetAttachmentAsync(attachmentEntry);
        Assert.NotNull(attachment);
        var line = attachment.Attachments2_Lines?.FirstOrDefault();
        if (line is null)
        {
            return;
        }

        using var download = await fixture.Client.DownloadAttachmentAsync(attachmentEntry, line.FullFileName);
        Assert.NotNull(download);

        var head = new byte[16];
        var read = await download.Content.ReadAsync(head);
        Assert.True(read > 0, $"SAP answered the $value read for '{line.FullFileName}' with an empty body.");
    }

    [SapFact]
    public async Task The_configured_sap_account_resolves_to_a_user()
    {
        // The service approver defaults to the session user, and the stage check needs its InternalKey.
        var user = await fixture.Client.GetSapUserByCodeAsync("manager");

        Assert.NotNull(user);
        Assert.True(user.InternalKey > 0);
    }
}
