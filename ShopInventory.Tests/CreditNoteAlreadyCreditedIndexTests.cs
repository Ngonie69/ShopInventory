using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Which open requests the queues hide: a draft is already credited when a credit note for the same
/// customer, Customer Ref. No. and total was entered no earlier than the draft, and still credits.
/// </summary>
/// <remarks>
/// The match blocks an approval and takes a row out of the queue, so it is deliberately narrow. Crate
/// returns of a round sum to the same customer are routine; a credit note entered before the draft was
/// raised is not the draft keyed twice; a cancelled credit note credits nobody.
/// </remarks>
public sealed class CreditNoteAlreadyCreditedIndexTests
{
    [Fact]
    public async Task Only_a_draft_with_a_live_credit_note_of_its_customer_reference_and_total_is_indexed()
    {
        var sap = new FakeQueue(
            requests:
            [
                Request(3110, 801), // keyed again the same day
                Request(3111, 802), // a credit note with another reference
                Request(3112, 803), // a credit note entered the day before the draft
                Request(3113, 804), // a credit note one cent different
                Request(3114, 805), // only a cancelled credit note and its cancellation
                Request(3115, 806), // a draft with no reference
                Request(3116, 807, SapApprovalRequestStatuses.Approved, stage: 9, originator: 31) // keyed again later
            ],
            drafts:
            [
                Draft(801, "800123"), Draft(802, "800124"), Draft(803, "800125"), Draft(804, "800126"),
                Draft(805, "800127"), Draft(806, null), Draft(807, "800128")
            ],
            creditNotes:
            [
                Note(1, "800123", "2026-08-15"),
                Note(2, "800999", "2026-08-15"),
                Note(3, "800125", "2026-08-14"),
                Note(4, "800126", "2026-08-15", total: 412.66m),
                Note(5, "800127", "2026-08-16", cancelStatus: "csYes"),
                Note(6, "800127", "2026-08-16", cancelStatus: "csCancellation"),
                Note(7, " 800128 ", "2026-09-30")
            ]);

        var index = await Index(sap).GetAsync(CancellationToken.None);

        Assert.Equal(new[] { 3110, 3116 }, index.Keys.Order().ToArray());
        Assert.Equal(1, index[3110].CreditNote.DocEntry);

        var later = index[3116];
        Assert.Equal(7, later.CreditNote.DocEntry);
        Assert.Equal(SapApprovalRequestStatuses.Approved, later.Status);
        Assert.Equal(9, later.CurrentStage);
        Assert.Equal(31, later.OriginatorId);
        Assert.Equal(new DateTime(2026, 8, 15), later.RaisedOn!.Value.Date);

        // The draft with no reference is never asked about, and only the credited drafts' requests are read.
        Assert.Equal(6, sap.AskedPairs);
        Assert.Equal(new[] { 801, 807 }, sap.AskedRequestsFor!.Order().ToArray());
    }

    [Fact]
    public async Task The_index_is_built_once_and_again_only_after_it_is_invalidated()
    {
        var sap = new FakeQueue([Request(3110, 801)], [Draft(801, "800123")], [Note(1, "800123", "2026-08-15")]);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await Index(sap, cache).GetAsync(CancellationToken.None);
        await Index(sap, cache).GetAsync(CancellationToken.None);
        Assert.Equal(1, sap.QueueReads);

        // Any scoped instance clears the shared entry, as a decision or an add does.
        Index(sap, cache).Invalidate();
        await Index(sap, cache).GetAsync(CancellationToken.None);
        Assert.Equal(2, sap.QueueReads);
    }

    [Fact]
    public void A_request_falls_in_a_queue_only_by_its_status_stage_raised_day_and_originator()
    {
        var entry = new AlreadyCreditedRequest(3110, SapApprovalRequestStatuses.Pending, 4, new DateTime(2026, 8, 15), 12, Note(1, "800123", "2026-08-15"));
        var pending = new[] { SapApprovalRequestStatuses.Pending };
        var washBay = new CreditNoteApprovalStageFilter(["Wash Bay Approvals"], new HashSet<int> { 4 });

        Assert.True(entry.FallsIn(pending, CreditNoteApprovalStageFilter.EveryStage, null));
        Assert.True(entry.FallsIn(pending, washBay, new CreditNoteApprovalRequestFilter(new DateTime(2026, 8, 15), new DateTime(2026, 8, 15), 12)));
        Assert.False(entry.FallsIn([SapApprovalRequestStatuses.Approved], CreditNoteApprovalStageFilter.EveryStage, null));
        Assert.False(entry.FallsIn(pending, new CreditNoteApprovalStageFilter(["Finance"], new HashSet<int> { 9 }), null));
        Assert.False(entry.FallsIn(pending, washBay, new CreditNoteApprovalRequestFilter(new DateTime(2026, 8, 16))));
        Assert.False(entry.FallsIn(pending, washBay, new CreditNoteApprovalRequestFilter(RaisedTo: new DateTime(2026, 8, 14))));
        Assert.False(entry.FallsIn(pending, washBay, new CreditNoteApprovalRequestFilter(OriginatorId: 31)));
    }

    private static CreditNoteAlreadyCreditedIndex Index(FakeQueue sap, IMemoryCache? cache = null) =>
        new(sap.AsClient(), cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<CreditNoteAlreadyCreditedIndex>.Instance);

    private static SAPApprovalRequest Request(int code, int draftEntry, string status = SapApprovalRequestStatuses.Pending, int stage = 4, int originator = 12) => new()
    {
        Code = code,
        ObjectType = SapObjectTypes.CreditNote,
        Status = status,
        DraftEntry = draftEntry,
        CurrentStage = stage,
        OriginatorID = originator,
        CreationDate = "2026-08-15T00:00:00Z"
    };

    private static SAPCreditNote Draft(int docEntry, string? numAtCard) => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry + 60000,
        CardCode = "C00477",
        NumAtCard = numAtCard,
        DocTotal = 412.65m,
        CreationDate = "2026-08-15T00:00:00Z",
        DocumentStatus = SapDocumentStatuses.Open,
        Cancelled = SapYesNo.No,
        AuthorizationStatus = SapDocumentAuthorizationStatuses.Pending,
        DocObjectCode = SapDocObjectCodes.CreditNotes
    };

    private static SAPCreditNote Note(int docEntry, string numAtCard, string entered, decimal total = 412.65m, string cancelStatus = "csNo") => new()
    {
        DocEntry = docEntry,
        DocNum = docEntry + 70000,
        CardCode = "C00477",
        NumAtCard = numAtCard,
        DocTotal = total,
        CreationDate = entered + "T00:00:00Z",
        Cancelled = cancelStatus == "csYes" ? SapYesNo.Yes : SapYesNo.No,
        CancelStatus = cancelStatus
    };

    /// <summary>The three reads an index build makes — held drafts, credit notes, requests — answered from lists.</summary>
    private sealed class FakeQueue(List<SAPApprovalRequest> requests, List<SAPCreditNote> drafts, List<SAPCreditNote> creditNotes)
    {
        public int QueueReads { get; private set; }
        public int AskedPairs { get; private set; }
        public IReadOnlyCollection<int>? AskedRequestsFor { get; private set; }

        public ISAPServiceLayerClient AsClient() => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetOpenHeldCreditNoteDraftsAsync) => ReadHeldDrafts(),
            nameof(ISAPServiceLayerClient.GetCreditNotesByCustomerTotalsAsync) => ReadCreditNotes((IReadOnlyCollection<SapCreditNoteTotalKey>)args![0]!),
            nameof(ISAPServiceLayerClient.GetCreditNoteApprovalRequestsForDraftsAsync) => ReadRequests((IReadOnlyCollection<int>)args![1]!),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        private Task<List<SAPCreditNote>> ReadHeldDrafts()
        {
            QueueReads++;
            return Task.FromResult(drafts.ToList());
        }

        private Task<List<SAPApprovalRequest>> ReadRequests(IReadOnlyCollection<int> draftEntries)
        {
            AskedRequestsFor = draftEntries;
            return Task.FromResult(requests.Where(request => draftEntries.Contains(request.DraftEntry ?? 0)).ToList());
        }

        // As SAP answers: the customer and total match, cancellations are left out; the day is the caller's.
        private Task<List<SAPCreditNote>> ReadCreditNotes(IReadOnlyCollection<SapCreditNoteTotalKey> keys)
        {
            AskedPairs += keys.Count;
            return Task.FromResult(creditNotes
                .Where(note => note.CancelStatus == "csNo" && keys.Any(key => key.CardCode == note.CardCode && key.DocTotal == note.DocTotal))
                .ToList());
        }
    }
}
